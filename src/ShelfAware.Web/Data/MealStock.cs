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

    /// <summary>What <see cref="ApplyAsync"/> is about to do, without doing it. §13.3 requires the tap to
    /// SHOW its decrement first: the amount is approximate by design (recipe quantities are free-form
    /// strings the app must not parse), and an approximate change to a hand-maintained number cannot
    /// also be a silent one. A plan that needs no confirmation means this recipe touches no counted item
    /// — then the tap goes straight through, because there is nothing to warn about.</summary>
    public static async Task<Plan> PlanAsync(
        ShelfAwareDbContext db, Recipe recipe, CancellationToken ct = default)
    {
        var (products, ambiguous) = await CountedMainsAsync(db, recipe, ct);
        return new Plan(
            [.. products.Select(p => new Take(
                p.Id, p.Name, p.DefaultUnit, p.QuantityOnHand!.Value,
                TypicalPackage.Of(p.Purchases.Select(x => x.Quantity))))],
            ambiguous);
    }

    /// <summary>Take the packages off. Goes through <see cref="StockLedger.Remove"/>, which has no path
    /// to a signal at all: a machine decrement that arrives at zero is a hypothesis for the product page
    /// to raise, never an outage the human never reported (§13.4).</summary>
    public static async Task ApplyAsync(ShelfAwareDbContext db, Recipe recipe, CancellationToken ct = default)
    {
        var (products, _) = await CountedMainsAsync(db, recipe, ct);
        foreach (var product in products)
        {
            StockLedger.Remove(product, TypicalPackage.Of(product.Purchases.Select(x => x.Quantity)));
        }
    }

    /// <summary>The counted products this recipe's MAIN ingredients resolve to — ONE definition, shared
    /// by the preview and the write, so the confirm panel can never promise a decrement the commit
    /// doesn't make.
    /// <para>⚠️ It asks <see cref="IngredientMatcher"/>, the SAME question the ✓/🛒 mark on the row above
    /// asks. Matching on <c>MatchedProduct</c> alone (as this first did) meant a row could show "you have
    /// this" — satisfied by an on-hand product of the same specific food, or by a curated "also works as"
    /// — while the tap beneath it decremented NOTHING, because the grounded link was null or named
    /// something else. Two rules for "which product does this ingredient mean" is the same
    /// screen-disagrees-with-engine fault this branch keeps finding; there is one rule now. It is also
    /// what lets a count on a product the recipe was saved BEFORE (census stock, §13.8) ever be
    /// maintained: nothing back-fills <c>MatchedProduct</c> when a product appears.</para>
    /// <para><b>Ambiguity is refused, not guessed.</b> The matcher is deliberately loose, so an ingredient
    /// can be covered by more than one counted product ("ground beef" by two cuts). Cooking one meal must
    /// not take a package off each, and picking one silently would be arbitrary — so a main that resolves
    /// to several counted products decrements none of them and is reported instead
    /// (<see cref="Ambiguity"/>), which the confirm panel shows so the human can correct it by hand. The
    /// grounded <c>MatchedProduct</c> wins outright when it names a counted product, which is exactly
    /// what it's for.</para>
    /// <para>Products at zero are excluded: <see cref="StockLedger.Remove"/> clamps there, so including
    /// them would put a confirmation in front of a decrement that provably changes nothing.</para>
    /// <para>Two queries rather than one, deliberately: the first reads the counted set's NAMES to decide
    /// what matches (in memory — the matcher can't run in SQL, and SQLite's <c>IN</c> is case-sensitive
    /// anyway), the second loads purchases only for the handful that matched, rather than dragging every
    /// purchase of every counted item across for the sake of one or two.</para></summary>
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

        var chosen = new HashSet<int>();
        var ambiguous = new List<Ambiguity>();
        foreach (var main in mains)
        {
            // The grounded link first — a human confirmed it, so it beats any inference.
            var exact = counted.FirstOrDefault(
                c => string.Equals(c.Name, main.MatchedProduct, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                chosen.Add(exact.Id);
                continue;
            }

            // Otherwise the makeability rule, asked one product at a time so we learn WHICH covers it.
            var covering = counted
                .Where(c => IngredientMatcher.IsSatisfied(
                    main.Name, main.MatchedProduct, [new PantryProduct(c.Name, c.Substitutes)]))
                .ToList();
            if (covering.Count == 1) chosen.Add(covering[0].Id);
            else if (covering.Count > 1)
                ambiguous.Add(new Ambiguity(main.Name, [.. covering.Select(c => c.Name).Order()]));
        }

        if (chosen.Count == 0) return ([], ambiguous);

        var products = await db.Products
            .Where(p => chosen.Contains(p.Id))
            .Include(p => p.Purchases)
            .ToListAsync(ct);
        return (products, ambiguous);
    }
}
