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
    /// <summary>Whether a FRESH, engine-trusted count is the authority on this product — a tracked quantity
    /// the engine has neither staled nor pinned. THE single definition of "a real count decides this",
    /// consumed by <see cref="InStock"/> as its deciding branch and reported by
    /// <see cref="EdibleInStockDetailed"/> as CountBacked, so a recipe's tick and its "you have this" /
    /// "likely" label can never answer it differently.
    /// <para>⚠️ A count answers "how many", never "are they still good" or "what did someone just tell us",
    /// so a STALE count (nobody has vouched for it in months, §13.5) and a PINNED item (an expiration label
    /// or an explicit <c>OutNow</c>) both strip the count of authority here — exactly as they do for
    /// buy-suppression. Reading the ENGINE'S <c>Pinned</c>/<c>CountLooksStale</c> rather than re-deriving
    /// that precedence is what stops recipes offering food the app knows is expired, or that the household
    /// has just reported running out of.</para></summary>
    private static bool CountGoverns(Product p, PredictionResult prediction) =>
        p is { TrackQuantity: true, QuantityOnHand: not null }
        && !prediction.CountLooksStale
        && !prediction.Pinned;

    /// <summary>Whether this product is stock a recipe can use.
    /// <para>When a fresh count <see cref="CountGoverns">governs</see>, it decides outright, in both
    /// directions — "real evidence beats a learned guess" (§13.5): a household that counted three has three,
    /// and a counted zero is out, whatever the rhythm infers. This is also the only thing that lets a count
    /// reach recipes at all for stock with no purchase history (bought before the app, elsewhere, gifted,
    /// bulk): such an item never leaves <c>Unknown</c>, so reading <c>Status</c> alone made its count
    /// invisible here — a counted 12 added nothing and a counted 0 removed nothing.</para>
    /// <para>Otherwise the rhythm decides: a stale count, or no count at all, defers to whether the engine
    /// thinks you've run out.</para>
    /// <para>⚠️ A count of zero withholding an item from recipes is a DISPLAY inference, and §13.4's rule
    /// is untouched by it: a derived zero still cannot write an <c>OutNow</c>. The cost of being wrong here
    /// is a red recipe row with a hint (see <see cref="EdibleOutOfStock"/>), not a false outage taught to
    /// the cadence engine.</para></summary>
    private static bool InStock(Product p, PredictionResult prediction) =>
        CountGoverns(p, prediction)
            ? p.QuantityOnHand > 0
            : prediction.Status != PredictionStatus.Overdue;

    /// <summary>Each tracked, edible product with the ONE prediction it drives and whether it's on hand.
    /// EVERY method below reads this rather than re-<c>Predict</c>ing or re-filtering, so a caller needing
    /// both lists (a recipe row wants the on-hand set for its tick and the run-out set for its hint) predicts
    /// and filters each product exactly once, and no second copy of the aisle filter or the on-hand rule can
    /// exist to drift.</summary>
    private static IEnumerable<(Product Product, PredictionResult Prediction, bool InStock)> Classify(
        IEnumerable<Product> products, DateOnly today, bool honorExpirations) =>
        products
            .Where(p => p.IsTracked && p.Category.IsEdible())
            .Select(p =>
            {
                var prediction = ReplenishmentPredictor.Predict(p, today, honorExpirations, honorQuantity: true);
                return (Product: p, Prediction: prediction, InStock: InStock(p, prediction));
            });

    public static IEnumerable<Product> EdibleInStock(IEnumerable<Product> products, DateOnly today, bool honorExpirations = false) =>
        Classify(products, today, honorExpirations).Where(x => x.InStock).Select(x => x.Product);

    /// <summary>The on-hand set, each flagged with whether a FRESH COUNT backs it (real evidence, §13.5)
    /// versus only the rhythm's not-overdue guess. Recipes read the flag to say "you have this" for a
    /// counted item but "likely" for a predicted one — the count is the difference between KNOWING and
    /// INFERRING, and a recipe that asserts the inference is the false-positive that erodes trust in the
    /// whole feature. Reads <see cref="Classify"/> like every sibling, so it re-predicts and re-filters
    /// nothing. <c>CountBacked</c> is exactly <see cref="CountGoverns"/>: for an item already in stock, the
    /// count backs it iff the count — not the rhythm — is what put it there, so a stale count or a pinned
    /// item (neither governs) never reads count-backed.</summary>
    public static IReadOnlyList<(Product Product, bool CountBacked)> EdibleInStockDetailed(
        IEnumerable<Product> products, DateOnly today, bool honorExpirations = false) =>
        Classify(products, today, honorExpirations)
            .Where(x => x.InStock)
            .Select(x => (x.Product, CountBacked: CountGoverns(x.Product, x.Prediction)))
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
        foreach (var (product, _, isIn) in Classify(products, today, honorExpirations))
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

    // ⚠️ THROWAWAY — deliberately UNTESTED code, only to prove the mutation-failure annotations fire on a
    // real PR. This branch is NOT for merge; it gets closed and deleted. No test covers this, so Stryker
    // marks its mutants NoCoverage and the score drops below 100 → the gate fails and annotates this line.
    public static bool DemoUncoveredForAnnotationProof(int n) => n > 0;
}
