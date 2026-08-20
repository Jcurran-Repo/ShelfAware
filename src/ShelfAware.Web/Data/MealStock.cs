using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
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
    /// <summary>One product the picker can offer for an ambiguous main — everything the modal needs to
    /// render a bubble ("Ground Chuck — 3.72 lb on hand") without a query of its own, plus the package
    /// the pick would take.</summary>
    public sealed record Candidate(int ProductId, string ProductName, string? DefaultUnit, decimal OnHand, decimal OnePackage);

    /// <param name="Ingredient">A main ingredient the app declines to decrement on its own — several
    /// counted products could be what it means, or its grounded product exists uncounted (see
    /// <see cref="CountedMainsAsync"/>). Reported with full candidates so the page can ASK on the spot
    /// (§13.3's picker): the human points at what came off the shelf, or clicks away and no count
    /// moves. Guessing silently would be arbitrary; a hidden refusal would leave a count quietly
    /// un-maintained.</param>
    public sealed record Ambiguity(string Ingredient, IReadOnlyList<Candidate> Candidates);

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
    /// contract now that nothing asks first, and it is what <see cref="Restore"/> reverses. Each take
    /// is <see cref="TakeOne"/>; a product the ledger didn't actually move is not reported.</summary>
    public static IReadOnlyList<Applied> Apply(Resolution resolution)
    {
        var applied = new List<Applied>();
        foreach (var product in resolution.Products)
        {
            if (TakeOne(product) is { } taken) applied.Add(taken);
        }
        return applied;
    }

    /// <summary>ONE take, the only definition — <see cref="Apply"/> makes it for every resolved main,
    /// the page makes it for the product a picker answer points at. One package per
    /// <see cref="TypicalPackage"/>, through <see cref="StockLedger.Remove"/>: clamps at zero, never
    /// writes a signal (§13.4 — a machine decrement arriving at zero is a hypothesis for the product
    /// page to raise, never an outage the human never reported), never touches the attestation clock.
    /// Returns the ACTUAL movement for the notice and the undo, or null when nothing moved — including
    /// a product that stopped qualifying since it was loaded. The caller owns SaveChanges.</summary>
    public static Applied? TakeOne(Product product)
    {
        if (product.QuantityOnHand is not { } before || before <= 0) return null;
        StockLedger.Remove(product, TypicalPackage.Of(product.Purchases.Select(x => x.Quantity)));
        var after = product.QuantityOnHand!.Value;
        return before == after ? null : new Applied(product.Id, product.Name, product.DefaultUnit, before - after, after);
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

    /// <summary>Reverse a logged meal on the CALLER's context (no save): remove the <see cref="MealEvent"/>,
    /// step its recipe's <see cref="Recipe.TimesEaten"/> back, and put the <paramref name="taken"/> packages
    /// back via <see cref="Restore"/>. Returns false when the meal is already gone — another surface removed
    /// it (an inline "↩ Undo", another device) — so nothing coherent is left to reverse; the caller turns
    /// that into "nothing to undo"/<c>UndoResult.Gone</c>.
    /// <para>The ONE reversal both surfaces run: the inline notice's Undo and the /history undo call this
    /// (via <c>MealLoggedHandler</c>), so "reverse a meal" has a single definition — the same
    /// static-on-db pattern <c>ProductRenameService.RenameOnAsync</c> uses, staging on the passed context
    /// so the reversal and the <c>ActivityEntry.UndoneAt</c> stamp commit in one transaction and no
    /// self-committing service runs mid-Peek.</para></summary>
    public static async Task<bool> ReverseMealOnAsync(
        ShelfAwareDbContext db, int mealEventId, IReadOnlyList<Applied> taken, CancellationToken ct = default)
    {
        var meal = await db.MealEvents.FindAsync([mealEventId], ct);
        if (meal is null) return false;

        db.MealEvents.Remove(meal);
        if (await db.Recipes.FindAsync([meal.RecipeId], ct) is { } recipe)
            recipe.TimesEaten = Math.Max(0, recipe.TimesEaten - 1);

        var ids = taken.Select(t => t.ProductId).ToList();
        var products = ids.Count == 0
            ? []
            : await db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        Restore(products, taken);
        return true;
    }

    /// <summary>The counted products this recipe's MAIN ingredients resolve to — ONE definition, so the
    /// notice can never report a decrement the write didn't make.
    /// <para>⚠️ It asks <see cref="IngredientMatcher.Covering"/>, the SAME rule the ✓/🛒 mark on the row
    /// above is defined in terms of: two rules for "which product does this ingredient mean" let a row
    /// show "you have this" while the tap beneath it moved nothing. The grounded-link precedence lives
    /// in the matcher, not here. This shared rule is also what lets a count on a product the recipe was
    /// saved BEFORE (census stock, §13.8) be maintained at all: nothing back-fills
    /// <c>MatchedProduct</c> when a product appears.</para>
    /// <para><b>Ambiguity is ASKED, not guessed.</b> The matcher is deliberately loose, so an ingredient
    /// can be covered by more than one counted product ("ground beef" by two cuts). Cooking one meal must
    /// not take a package off each, and picking one silently would be arbitrary — so such a main
    /// decrements none of them here and comes back as an <see cref="Ambiguity"/> with full candidates,
    /// for the page's picker: the human points at what came off the shelf, or clicks away and no count
    /// moves.</para>
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

        // ⚠️ Identity KEYS, not raw names: a grounded MatchedProduct can be a punctuation variant of the
        // product's name ("Home Canned Sauce" for "Home-Canned Sauce"), the same identity
        // IngredientMatcher.Covering now folds — so a raw-name set would call a live grounded product
        // GONE and silently auto-take a token-matched stand-in. One identity definition, matching Covering.
        var countedKeys = counted.Select(c => ProductMatcher.IdentityKey(c.Name)).ToHashSet();

        // Every product identity in the household, counted or not — needed only to tell a grounded link
        // whose product exists-but-uncounted (a live pairing the household simply doesn't count —
        // decrementing a token-matched stand-in would be a guess) from one whose product is GONE
        // (renamed/deleted — a stale link, where the token fall-through is the whole §13.8 census
        // story and must stay automatic). Paid only when some grounded link actually points outside
        // the counted set: the common cases (no grounded link, or grounded to a counted product)
        // short-circuit on countedKeys and never consult it.
        var groundedOutside = mains.Any(m =>
            m.MatchedProduct is { Length: > 0 } g && !countedKeys.Contains(ProductMatcher.IdentityKey(g)));
        var allKeys = groundedOutside
            ? (await db.Products.Select(p => p.Name).ToListAsync(ct))
                .Select(ProductMatcher.IdentityKey).ToHashSet()
            : countedKeys;

        // IngredientMatcher.Covering already prefers the grounded MatchedProduct over an inference, so
        // there is no precedence to re-implement here — a pinned ingredient simply comes back as one
        // candidate. Ids ride beside the matcher's name-level candidates by REFERENCE, so two counted
        // rows sharing a name (no unique index exists; "Add anyway" is real) are simply two candidates
        // the picker can tell apart by their counts — never a key collision.
        var candidates = new List<PantryProduct>(counted.Count);
        var idOf = new Dictionary<PantryProduct, int>(ReferenceEqualityComparer.Instance);
        foreach (var c in counted)
        {
            var candidate = new PantryProduct(c.Name, c.Substitutes);
            candidates.Add(candidate);
            idOf[candidate] = c.Id;
        }

        // Pass 1: settle every main the app may take from WITHOUT asking — exactly one covering row,
        // and no live-but-uncounted grounded product standing beside it. When the grounded product
        // exists uncounted, any counted coverage reached by falling through it is a stand-in guess
        // (store pack or freezer stock?), so it goes to the picker even with one candidate.
        var chosen = new HashSet<int>();
        var unsettled = new List<(string Ingredient, IReadOnlyList<PantryProduct> Covering)>();
        foreach (var main in mains)
        {
            var covering = IngredientMatcher.Covering(main.Name, main.MatchedProduct, candidates);
            if (covering.Count == 0) continue;
            var groundedUncounted = false;
            if (main.MatchedProduct is { Length: > 0 } grounded)
            {
                var gk = ProductMatcher.IdentityKey(grounded);
                groundedUncounted = !countedKeys.Contains(gk) && allKeys.Contains(gk);
            }
            if (covering.Count == 1 && !groundedUncounted) chosen.Add(idOf[covering[0]]);
            else unsettled.Add((main.Name, covering));
        }

        // Pass 2, and it needs the COMPLETE chosen set — which is why it can't fold into the loop above.
        // If a candidate is already being decremented (another main was pinned to it), this ingredient is
        // covered by that same package and there is nothing to ask about: asking anyway would offer a
        // product the take list is already taking. Grouped by ingredient name so a recipe that lists one
        // main twice doesn't ask twice.
        var open = unsettled
            .Where(u => !u.Covering.Any(c => chosen.Contains(idOf[c])))
            .GroupBy(u => u.Ingredient, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Ingredient: g.Key, Ids: g.First().Covering.Select(c => idOf[c]).Distinct().ToList()))
            .ToList();

        // One load for everything the caller might touch — the takes AND the picker's candidates, with
        // purchases so each carries its own typical package. Re-filtered to rows that still qualify:
        // the window between the two queries belongs to other circuits, and a product deleted or
        // emptied in it is DROPPED (as the pre-picker code tolerated), never indexed into a crash.
        var wanted = chosen.Union(open.SelectMany(o => o.Ids)).ToList();
        if (wanted.Count == 0) return ([], []);
        var byId = (await db.Products
                .Where(p => wanted.Contains(p.Id))
                .Include(p => p.Purchases)
                .ToListAsync(ct))
            .Where(p => p is { TrackQuantity: true, QuantityOnHand: > 0 })
            .ToDictionary(p => p.Id);

        var ambiguous = open
            .Select(o => new Ambiguity(
                o.Ingredient,
                [.. o.Ids.Where(byId.ContainsKey).Select(id => byId[id])
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)
                    .Select(p => new Candidate(
                        p.Id, p.Name, p.DefaultUnit, p.QuantityOnHand!.Value,
                        TypicalPackage.Of(p.Purchases.Select(x => x.Quantity))))]))
            .Where(a => a.Candidates.Count > 0) // every candidate vanished → nothing left to ask
            .ToList();

        return ([.. chosen.Where(byId.ContainsKey).Select(id => byId[id])], ambiguous);
    }
}
