using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;

namespace ShelfAware.Core.Recipes;

/// <summary>
/// Which products count as "on hand" for recipe reasoning: tracked, in an EDIBLE aisle, and not one the
/// engine thinks you've run out of. One definition shared by the Recipes page and the recipe adapter so
/// the two can't drift.
/// </summary>
public static class PantryOnHand
{
    /// <summary>Whether this product is stock a recipe can use.
    /// <para>A FRESH count decides it outright, in both directions — "real evidence beats a learned guess"
    /// (§13.5), and a household that counted three has three whatever a rhythm infers. This is also the
    /// only thing that lets a count reach recipes at all for stock with no purchase history (bought before
    /// the app, elsewhere, gifted, bulk): such an item never leaves <c>Unknown</c>, so reading
    /// <c>Status</c> alone made its count invisible here — a counted 12 added nothing and a counted 0
    /// removed nothing.</para>
    /// <para>A STALE count defers to the rhythm, for the same reason it stops suppressing: a number nobody
    /// has vouched for in months is not evidence any more.</para>
    /// <para>⚠️ A count of zero withholding an item from recipes is a DISPLAY inference, and §13.4's rule
    /// is untouched by it: a derived zero still cannot write an <c>OutNow</c>. The cost of being wrong here
    /// is a red recipe row with a hint (see <see cref="EdibleOutOfStock"/>), not a false outage taught to
    /// the cadence engine.</para></summary>
    private static bool InStock(Product p, DateOnly today, bool honorExpirations)
    {
        var prediction = ReplenishmentPredictor.Predict(p, today, honorExpirations, honorQuantity: true);
        if (p is { TrackQuantity: true, QuantityOnHand: { } onHand } && !prediction.CountLooksStale)
            return onHand > 0;
        return prediction.Status != PredictionStatus.Overdue;
    }

    public static IEnumerable<Product> EdibleInStock(IEnumerable<Product> products, DateOnly today, bool honorExpirations = false) =>
        products.Where(p =>
            p.IsTracked &&
            p.Category.IsEdible() &&
            InStock(p, today, honorExpirations));

    /// <summary>The other side of the same rule: tracked, edible products the engine thinks you've RUN OUT
    /// of. These are exactly the items <see cref="EdibleInStock"/> silently drops — surfaced so a recipe
    /// row can say "you may still have this, it's just due for a re-buy" instead of a bare red mark.
    /// With <paramref name="honorExpirations"/> that includes EXPIRED items (an expired chicken must not
    /// count as on-hand chicken) — the two methods stay exact complements by construction, which is why
    /// this negates <see cref="InStock"/> rather than re-deriving the rule.</summary>
    public static IEnumerable<Product> EdibleOutOfStock(IEnumerable<Product> products, DateOnly today, bool honorExpirations = false) =>
        products.Where(p =>
            p.IsTracked &&
            p.Category.IsEdible() &&
            !InStock(p, today, honorExpirations));

    /// <summary>The third way a covering product can be invisible: edible but UNTRACKED. Untracked items
    /// are excluded from on-hand and run-out alike (the engine isn't allowed to watch them), which
    /// otherwise leaves a red recipe row with no explanation at all — surfaced so the row can say
    /// "you have this, but it's untracked" with a one-tap re-track.</summary>
    public static IEnumerable<Product> EdibleUntracked(IEnumerable<Product> products) =>
        products.Where(p => !p.IsTracked && p.Category.IsEdible());
}
