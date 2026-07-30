using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Web.Data;

/// <summary>What cooking a recipe takes off the shelf (DESIGN.md §13.3): one package of every MAIN
/// ingredient whose matched product the household counts.
/// <para>It lives here rather than inside <c>Recipes.razor</c> for the reason §13.7 already learned the
/// hard way — logic private to a page is logic no test can reach, and the last bug of this exact kind
/// (a report re-deriving a due date) shipped past a fully green suite. This one silently changes a
/// number the household maintains by hand, which is the last place to accept "green tests, untested
/// code".</para>
/// <para>Nothing here calls SaveChanges: the caller owns the transaction, because the decrement has to
/// land in the same save as the <c>MealEvent</c> it belongs to.</para>
/// </summary>
public static class MealStock
{
    /// <param name="Amount">One package, per <see cref="TypicalPackage"/> — 1 for a counted item, the
    /// household's own median pack for a weight item.</param>
    /// <param name="OnHand">What the count says right now, so a preview can show the subtraction rather
    /// than just its result.</param>
    public sealed record Take(int ProductId, string ProductName, string? DefaultUnit, decimal OnHand, decimal Amount)
    {
        /// <summary>Where the count lands. Clamped like <see cref="StockLedger"/> clamps it, so the
        /// preview promises exactly what the write performs.</summary>
        public decimal Remaining => Math.Max(0m, OnHand - Amount);
    }

    /// <summary>Whether a plan shown to the user still describes what would happen now. The preview and
    /// the commit are two separate user actions, so they run on two different <c>DbContext</c>s with an
    /// unbounded gap between them — a receipt confirm, another member's <c>set_quantity</c>, or a second
    /// cook in the same household can move a count in between. "The panel cannot promise what the write
    /// doesn't do" only holds if the write CHECKS, so the caller re-plans and compares before saving,
    /// and re-shows rather than writing when this returns false.</summary>
    public static bool Matches(Plan shown, Plan current) =>
        shown.Takes.Count == current.Takes.Count
        && shown.Ambiguous.Count == current.Ambiguous.Count
        && shown.Takes.OrderBy(t => t.ProductId)
            .Zip(current.Takes.OrderBy(t => t.ProductId))
            .All(pair => pair.First == pair.Second)
        && shown.Ambiguous.OrderBy(a => a.Ingredient, StringComparer.OrdinalIgnoreCase)
            .Zip(current.Ambiguous.OrderBy(a => a.Ingredient, StringComparer.OrdinalIgnoreCase))
            .All(pair => pair.First.Ingredient == pair.Second.Ingredient
                && pair.First.Candidates.SequenceEqual(pair.Second.Candidates));

    /// <param name="Ingredient">A main ingredient covered by more than one counted product, so the app
    /// refuses to guess which package to take. Reported so the confirm panel can say so — a decrement it
    /// declines to make is exactly as much a thing the human must be told about as one it makes.</param>
    public sealed record Ambiguity(string Ingredient, IReadOnlyList<string> Candidates);

    /// <param name="Ambiguous">Mains the app would not decrement because several counted products cover
    /// them. Never empty-checked away: a plan is worth confirming if it has takes OR ambiguities.</param>
    public sealed record Plan(IReadOnlyList<Take> Takes, IReadOnlyList<Ambiguity> Ambiguous)
    {
        /// <summary>Whether this needs the human's eyes before anything is written.</summary>
        public bool NeedsConfirmation => Takes.Count > 0 || Ambiguous.Count > 0;
    }

    /// <summary>The loaded products a cook would decrement, plus the mains it refuses to guess at. Held so
    /// the caller can <see cref="Describe"/> it, compare, and then <see cref="Apply"/> the SAME objects —
    /// one resolution and one pair of queries per tap, and the write is literally the thing that was
    /// described rather than a second lookup that agrees by luck.</summary>
    public sealed record Resolution(IReadOnlyList<Product> Products, IReadOnlyList<Ambiguity> Ambiguous);

    /// <summary>Work out what cooking this recipe would take off the shelf, without taking it.</summary>
    public static async Task<Resolution> ResolveAsync(
        ShelfAwareDbContext db, Recipe recipe, CancellationToken ct = default)
    {
        var (products, ambiguous) = await CountedMainsAsync(db, recipe, ct);
        return new Resolution(products, ambiguous);
    }

    /// <summary>What <see cref="Apply"/> is about to do, described for a human. §13.3 requires the tap to
    /// SHOW its decrement first: the amount is approximate by design (recipe quantities are free-form
    /// strings the app must not parse), and an approximate change to a hand-maintained number cannot
    /// also be a silent one. A plan that needs no confirmation means this recipe touches no counted item
    /// — then the tap goes straight through, because there is nothing to warn about.
    /// <para>A pure projection, so the description and the write cannot describe different work.</para></summary>
    public static Plan Describe(Resolution resolution) =>
        new(
            [.. resolution.Products.Select(p => new Take(
                p.Id, p.Name, p.DefaultUnit, p.QuantityOnHand!.Value,
                TypicalPackage.Of(p.Purchases.Select(x => x.Quantity))))],
            resolution.Ambiguous);

    /// <summary>Resolve and describe in one step, for a caller that only wants to look.</summary>
    public static async Task<Plan> PlanAsync(
        ShelfAwareDbContext db, Recipe recipe, CancellationToken ct = default) =>
        Describe(await ResolveAsync(db, recipe, ct));

    /// <summary>Take the packages off. Goes through <see cref="StockLedger.Remove"/>, which has no path
    /// to a signal at all: a machine decrement that arrives at zero is a hypothesis for the product page
    /// to raise, never an outage the human never reported (§13.4).</summary>
    public static void Apply(Resolution resolution)
    {
        foreach (var product in resolution.Products)
        {
            StockLedger.Remove(product, TypicalPackage.Of(product.Purchases.Select(x => x.Quantity)));
        }
    }

    /// <summary>The counted products this recipe's MAIN ingredients resolve to — ONE definition, shared
    /// by the preview and the write, so the confirm panel can never promise a decrement the commit
    /// doesn't make.
    /// <para>⚠️ It asks <see cref="IngredientMatcher.Covering"/>, the SAME rule the ✓/🛒 mark on the row
    /// above is defined in terms of. Matching on <c>MatchedProduct</c> alone (as this first did) meant a
    /// row could show "you have this" — satisfied by an on-hand product of the same specific food, or by a
    /// curated "also works as" — while the tap beneath it decremented NOTHING, because the grounded link
    /// was null or named something else. Two rules for "which product does this ingredient mean" is the
    /// same screen-disagrees-with-engine fault this branch keeps finding; there is one rule now, and the
    /// grounded-link precedence lives in the matcher rather than being re-implemented here. It is also
    /// what lets a count on a product the recipe was saved BEFORE (census stock, §13.8) ever be
    /// maintained: nothing back-fills <c>MatchedProduct</c> when a product appears.</para>
    /// <para><b>Ambiguity is refused, not guessed.</b> The matcher is deliberately loose, so an ingredient
    /// can be covered by more than one counted product ("ground beef" by two cuts). Cooking one meal must
    /// not take a package off each, and picking one silently would be arbitrary — so a main that resolves
    /// to several counted products decrements none of them and is reported instead
    /// (<see cref="Ambiguity"/>), which the confirm panel shows so the human can correct it by hand.</para>
    /// <para>Products at zero are excluded: <see cref="StockLedger.Remove"/> clamps there, so including
    /// them would put a confirmation in front of a decrement that provably changes nothing.</para>
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
