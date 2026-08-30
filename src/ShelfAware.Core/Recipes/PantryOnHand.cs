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
    /// <para>⚠️ <b>A PINNED item is out, whatever the count says.</b> A count answers "how many", never
    /// "are they still good" or "what did someone just tell us" — so an expiration label and an explicit
    /// <c>OutNow</c> both beat it, exactly as they do for buy-suppression in §13.5. Skipping this check let
    /// recipes offer to cook with food the app knew was expired, and with food the household had just
    /// reported running out of. Reading the ENGINE'S pin rather than re-deriving the precedence is what
    /// keeps the two in step.</para>
    /// <para>⚠️ A count of zero withholding an item from recipes is a DISPLAY inference, and §13.4's rule
    /// is untouched by it: a derived zero still cannot write an <c>OutNow</c>. The cost of being wrong here
    /// is a red recipe row with a hint (see <see cref="EdibleOutOfStock"/>), not a false outage taught to
    /// the cadence engine.</para></summary>
    private static bool InStock(Product p, PredictionResult prediction) =>
        p is { TrackQuantity: true, QuantityOnHand: { } onHand }
        && !prediction.CountLooksStale
        && !prediction.Pinned
            ? onHand > 0
            : prediction.Status != PredictionStatus.Overdue;

    /// <summary>Tracked, edible products split into the two lists by ONE prediction each — the pair used to
    /// call <c>Predict</c> per product per list, so a page needing both (a recipe row needs the on-hand set
    /// for its tick and the run-out set for its hint) predicted everything twice per render.</summary>
    private static IEnumerable<(Product Product, bool InStock)> Classify(
        IEnumerable<Product> products, DateOnly today, bool honorExpirations) =>
        products
            .Where(p => p.IsTracked && p.Category.IsEdible())
            .Select(p => (p, InStock(p, ReplenishmentPredictor.Predict(p, today, honorExpirations, honorQuantity: true))));

    public static IEnumerable<Product> EdibleInStock(IEnumerable<Product> products, DateOnly today, bool honorExpirations = false) =>
        Classify(products, today, honorExpirations).Where(x => x.InStock).Select(x => x.Product);

    /// <summary>The on-hand set, each flagged with whether a FRESH COUNT backs it (real evidence, §13.5)
    /// versus only the rhythm's not-overdue guess. Recipes read the flag to say "you have this" for a
    /// counted item but "likely" for a predicted one — the count is the difference between KNOWING and
    /// INFERRING, and a recipe that asserts the inference is the false-positive that erodes trust in the
    /// whole feature. One pass, so a caller gets both without re-Predicting what a bare
    /// <see cref="EdibleInStock"/> call already computed. <c>CountBacked</c> is exactly the first branch of
    /// <see cref="InStock"/> — an in-stock item is count-backed iff a fresh count (not the rhythm) put it
    /// there; a stale count or a pinned item never reads count-backed.</summary>
    public static IReadOnlyList<(Product Product, bool CountBacked)> EdibleInStockDetailed(
        IEnumerable<Product> products, DateOnly today, bool honorExpirations = false) =>
        products
            .Where(p => p.IsTracked && p.Category.IsEdible())
            .Select(p => (Product: p, Prediction: ReplenishmentPredictor.Predict(p, today, honorExpirations, honorQuantity: true)))
            .Where(x => InStock(x.Product, x.Prediction))
            .Select(x => (x.Product, CountBacked:
                x.Product is { TrackQuantity: true, QuantityOnHand: > 0 } && !x.Prediction.CountLooksStale && !x.Prediction.Pinned))
            .ToList();

    /// <summary>The other side of the same rule: tracked, edible products the engine thinks you've RUN OUT
    /// of. These are exactly the items <see cref="EdibleInStock"/> silently drops — surfaced so a recipe
    /// row can say "you may still have this, it's just due for a re-buy" instead of a bare red mark.
    /// With <paramref name="honorExpirations"/> that includes EXPIRED items (an expired chicken must not
    /// count as on-hand chicken) — the two methods stay exact complements by construction, which is why
    /// this negates <see cref="InStock"/> rather than re-deriving the rule.</summary>
    public static IEnumerable<Product> EdibleOutOfStock(IEnumerable<Product> products, DateOnly today, bool honorExpirations = false) =>
        Classify(products, today, honorExpirations).Where(x => !x.InStock).Select(x => x.Product);

    /// <summary>Both lists from ONE pass, for a caller that needs them together — which is the common case,
    /// since a recipe row shows a tick from the first and its "you may still have this" hint from the
    /// second. Same predicate as the two methods above, so no third definition of on-hand exists.</summary>
    public static (List<Product> InStock, List<Product> OutOfStock) EdibleSplit(
        IEnumerable<Product> products, DateOnly today, bool honorExpirations = false)
    {
        var inStock = new List<Product>();
        var outOfStock = new List<Product>();
        foreach (var (product, isIn) in Classify(products, today, honorExpirations))
        {
            (isIn ? inStock : outOfStock).Add(product);
        }
        return (inStock, outOfStock);
    }

    /// <summary>The third way a covering product can be invisible: edible but UNTRACKED. Untracked items
    /// are excluded from on-hand and run-out alike (the engine isn't allowed to watch them), which
    /// otherwise leaves a red recipe row with no explanation at all — surfaced so the row can say
    /// "you have this, but it's untracked" with a one-tap re-track.</summary>
    public static IEnumerable<Product> EdibleUntracked(IEnumerable<Product> products) =>
        products.Where(p => !p.IsTracked && p.Category.IsEdible());
}
