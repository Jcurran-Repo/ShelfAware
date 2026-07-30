using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Web.Data;

/// <summary>What cooking a recipe takes off the shelf (DESIGN.md §13.3): one package of every MAIN
/// ingredient whose matched product the household counts.
/// <para>§13.3's honesty contract is <b>tell, don't ask</b>: the tap commits in one go and the caller
/// shows exactly what was taken, with <see cref="Restore"/> as the one-tap way back. The decrement is
/// approximate by design (recipe quantities are free-form strings the app must not parse), so it must
/// never be silent — but it also must not interrogate: a confirmation shown on every cook of the same
/// stew is read once and blown through forever after, which protects nothing and costs every tap.</para>
/// <para>It lives here rather than inside <c>Recipes.razor</c> because logic private to a page is logic
/// no test can reach, and this path changes a hand-maintained number without being asked to.</para>
/// <para>Nothing here calls SaveChanges: the caller owns the transaction, because the decrement has to
/// land in the same save as the <c>MealEvent</c> it belongs to (and the restore in the same save as
/// that event's removal).</para>
/// </summary>
public static class MealStock
{
    /// <param name="Ingredient">A main ingredient covered by more than one counted product, so the app
    /// refuses to guess which package to take. Reported so the after-the-tap notice can say so — a
    /// decrement it declines to make is exactly as much a thing the human must be told about as one it
    /// makes.</param>
    public sealed record Ambiguity(string Ingredient, IReadOnlyList<string> Candidates);

    /// <summary>One product <see cref="Apply"/> actually moved. <paramref name="Taken"/> is the ACTUAL
    /// change, not the nominal package — when the ledger clamps at zero (half a pack left, one taken)
    /// they differ, and an undo that re-adds the nominal amount would invent stock. Carries what the
    /// notice needs so the page holds no arithmetic of its own.</summary>
    public sealed record Applied(int ProductId, string ProductName, string? DefaultUnit, decimal Taken, decimal Remaining);

    /// <summary>The loaded products a cook decrements, plus the mains it refuses to guess at. Held so the
    /// caller applies the SAME objects the resolution found — one pair of queries per tap.</summary>
    public sealed record Resolution(IReadOnlyList<Product> Products, IReadOnlyList<Ambiguity> Ambiguous);

    /// <summary>Work out what cooking this recipe would take off the shelf, without taking it.</summary>
    public static async Task<Resolution> ResolveAsync(
        ShelfAwareDbContext db, Recipe recipe, CancellationToken ct = default)
    {
        var (products, ambiguous) = await CountedMainsAsync(db, recipe, ct);
        return new Resolution(products, ambiguous);
    }

    /// <summary>Take the packages off, and report exactly what was taken — the report IS the honesty
    /// contract now that nothing asks first, and it is what <see cref="Restore"/> reverses. Goes through
    /// <see cref="StockLedger.Remove"/>, which has no path to a signal at all: a machine decrement that
    /// arrives at zero is a hypothesis for the product page to raise, never an outage the human never
    /// reported (§13.4). A product the ledger didn't actually move is not reported.</summary>
    public static IReadOnlyList<Applied> Apply(Resolution resolution)
    {
        var applied = new List<Applied>();
        foreach (var product in resolution.Products)
        {
            var before = product.QuantityOnHand!.Value;
            StockLedger.Remove(product, TypicalPackage.Of(product.Purchases.Select(x => x.Quantity)));
            var after = product.QuantityOnHand!.Value;
            if (before != after)
            {
                applied.Add(new Applied(product.Id, product.Name, product.DefaultUnit, before - after, after));
            }
        }
        return applied;
    }

    /// <summary>Undo one <see cref="Apply"/>: put back the ACTUAL amounts it took, onto freshly loaded
    /// products (the undo is its own user action on its own context). Adding back commutes with anything
    /// that moved the counts in between — a receipt's +N, another cook — so no compare-and-refuse step
    /// is needed; the one asymmetry is a product whose counting stopped meanwhile, where the ledger's
    /// own gate leaves the dormant number alone rather than resurrecting it.</summary>
    public static void Restore(IReadOnlyList<Product> products, IReadOnlyList<Applied> applied)
    {
        var byId = products.ToDictionary(p => p.Id);
        foreach (var take in applied)
        {
            if (byId.TryGetValue(take.ProductId, out var product)) StockLedger.Add(product, take.Taken);
        }
    }

    /// <summary>The counted products this recipe's MAIN ingredients resolve to — ONE definition, so the
    /// notice can never report a decrement the write didn't make.
    /// <para>⚠️ It asks <see cref="IngredientMatcher.Covering"/>, the SAME rule the ✓/🛒 mark on the row
    /// above is defined in terms of: two rules for "which product does this ingredient mean" let a row
    /// show "you have this" while the tap beneath it moved nothing. The grounded-link precedence lives
    /// in the matcher, not here. This shared rule is also what lets a count on a product the recipe was
    /// saved BEFORE (census stock, §13.8) be maintained at all: nothing back-fills
    /// <c>MatchedProduct</c> when a product appears.</para>
    /// <para><b>Ambiguity is refused, not guessed.</b> The matcher is deliberately loose, so an ingredient
    /// can be covered by more than one counted product ("ground beef" by two cuts). Cooking one meal must
    /// not take a package off each, and picking one silently would be arbitrary — so a main that resolves
    /// to several counted products decrements none of them and is reported instead
    /// (<see cref="Ambiguity"/>), so the human can correct it by hand.</para>
    /// <para>Products at zero are excluded: <see cref="StockLedger.Remove"/> clamps there, so nothing
    /// would change and nothing is worth reporting.</para>
    /// <para>Two queries rather than one, deliberately: the first reads the counted set's names and
    /// substitute phrases — everything the matcher needs and nothing more — because the matcher can't run
    /// in SQL (and SQLite's <c>IN</c> is case-sensitive anyway); the second loads purchases only for the
    /// handful that matched, rather than dragging every purchase of every counted item across for the sake
    /// of one or two.</para></summary>
    private static async Task<(IReadOnlyList<Product> Products, IReadOnlyList<Ambiguity> Ambiguous)>
        CountedMainsAsync(ShelfAwareDbContext db, Recipe recipe, CancellationToken ct)
    {
        var mains = recipe.MainIngredients
            .Where(i => !string.IsNullOrWhiteSpace(i.Name) || !string.IsNullOrWhiteSpace(i.MatchedProduct))
            .ToList();
        if (mains.Count == 0) return ([], []);

        var counted = await db.Products
            .Where(p => p.TrackQuantity && p.QuantityOnHand > 0)
            .Select(p => new { p.Id, p.Name, Substitutes = p.Substitutes.Select(s => s.Value).ToList() })
            .ToListAsync(ct);
        if (counted.Count == 0) return ([], []);

        // IngredientMatcher.Covering already prefers the grounded MatchedProduct over an inference, so
        // there is no precedence to re-implement here — a pinned ingredient simply comes back as one
        // candidate. Keyed by product NAME because that is what the matcher speaks.
        // A name shared by two counted rows maps to NULL: the schema has no unique index on product
        // names (the duplicate guard is a UI prompt with an explicit "Add anyway"), and a name-keyed
        // matcher cannot say WHICH row such a name means — so its ingredient is refused like any other
        // ambiguity, rather than a dictionary collision taking down every "Ate it" in the household.
        var byName = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in counted)
        {
            byName[c.Name] = byName.ContainsKey(c.Name) ? null : c.Id;
        }
        var candidates = counted.Select(c => new PantryProduct(c.Name, c.Substitutes)).ToList();

        // Pass 1: settle every main that resolves to exactly one counted product — one covering name,
        // and that name addressing exactly one row. Candidates are reported by DISTINCT name: two rows
        // sharing one name are one answer to "which product", not two.
        var chosen = new HashSet<int>();
        var unsettled = new List<(string Ingredient, List<string> Candidates)>();
        foreach (var main in mains)
        {
            var covering = IngredientMatcher.Covering(main.Name, main.MatchedProduct, candidates);
            if (covering.Count == 1 && byName[covering[0].Name] is { } only) chosen.Add(only);
            else if (covering.Count > 0)
                unsettled.Add((main.Name,
                    [.. covering.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order()]));
        }

        // Pass 2, and it needs the COMPLETE chosen set — which is why it can't fold into the loop above.
        // If a candidate is already being decremented (another main was pinned to it), this ingredient is
        // covered by that same package and there is nothing to warn about: reporting it anyway put a
        // product in the panel's "not touching these" list while the panel's own take list was touching it.
        // Grouped by ingredient name so a recipe that lists one main twice doesn't say so twice.
        var ambiguous = unsettled
            .Where(u => !u.Candidates.Any(name => byName[name] is { } id && chosen.Contains(id)))
            .GroupBy(u => u.Ingredient, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Ambiguity(g.Key, g.First().Candidates))
            .ToList();

        if (chosen.Count == 0) return ([], ambiguous);

        var products = await db.Products
            .Where(p => chosen.Contains(p.Id))
            .Include(p => p.Purchases)
            .ToListAsync(ct);
        return (products, ambiguous);
    }
}
