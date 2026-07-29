using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;

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

    /// <summary>What <see cref="ApplyAsync"/> is about to do, without doing it. §13.3 requires the tap to
    /// SHOW its decrement first: the amount is approximate by design (recipe quantities are free-form
    /// strings the app must not parse), and an approximate change to a hand-maintained number cannot
    /// also be a silent one. Empty means this recipe touches no counted item — then the tap goes
    /// straight through, because there is nothing to warn about.</summary>
    public static async Task<IReadOnlyList<Take>> PlanAsync(
        ShelfAwareDbContext db, Recipe recipe, CancellationToken ct = default) =>
        (await CountedMainsAsync(db, recipe, ct))
            .Select(p => new Take(
                p.Id, p.Name, p.DefaultUnit, p.QuantityOnHand!.Value,
                TypicalPackage.Of(p.DefaultUnit, p.Purchases.Select(x => x.Quantity))))
            .ToList();

    /// <summary>Take the packages off. Goes through <see cref="StockLedger.Remove"/>, which has no path
    /// to a signal at all: a machine decrement that arrives at zero is a hypothesis for the product page
    /// to raise, never an outage the human never reported (§13.4).</summary>
    public static async Task ApplyAsync(ShelfAwareDbContext db, Recipe recipe, CancellationToken ct = default)
    {
        foreach (var product in await CountedMainsAsync(db, recipe, ct))
        {
            StockLedger.Remove(product, TypicalPackage.Of(product.DefaultUnit, product.Purchases.Select(x => x.Quantity)));
        }
    }

    /// <summary>The counted products this recipe's MAIN ingredients resolve to — ONE definition, shared
    /// by the preview and the write, so the confirm panel can never promise a decrement the commit
    /// doesn't make.</summary>
    private static async Task<IReadOnlyList<Product>> CountedMainsAsync(
        ShelfAwareDbContext db, Recipe recipe, CancellationToken ct)
    {
        var names = recipe.MainIngredients
            .Select(i => i.MatchedProduct)
            .OfType<string>()
            .Where(n => n.Length > 0)
            .ToList();
        if (names.Count == 0) return [];

        // Load the counted set — bounded by design, since counting is opt-in per product — and match in
        // memory. SQLite's `IN` is case-SENSITIVE, so matching in SQL would silently skip a product
        // whose casing has drifted from the MatchedProduct captured at save time, and the failure mode
        // is the worst kind: no error, the count simply never moves.
        var counted = await db.Products
            .Where(p => p.TrackQuantity && p.QuantityOnHand != null)
            .Include(p => p.Purchases)
            .ToListAsync(ct);

        return counted
            .Where(p => names.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }
}
