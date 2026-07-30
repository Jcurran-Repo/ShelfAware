namespace ShelfAware.Core.Domain;

/// <summary>
/// How much "one package" of a product is, for the decrements that can't know (DESIGN.md §13.3).
/// <para>Cooking a recipe takes one package off the count. For a counted item that's the number 1. For a
/// WEIGHT item it can't be: deducting 1 lb would be arbitrary — a pound is not a unit of anything about
/// how this household buys — so it's the median of that product's per-purchase quantities, and a
/// household whose ground beef arrives in 1.24 lb packs deducts 1.24.</para>
/// <para><b>The quantities themselves say which kind of product this is</b>, and §13.1 already says why:
/// "decimal because weight items are already fractional". A whole-number median means the numbers are
/// COUNTS, so one of them is 1; a fractional median means they're a continuous measure, so one package is
/// that median.</para>
/// <para>⚠️ It deliberately does NOT read <see cref="Product.DefaultUnit"/>. That field is a display
/// label for <see cref="Shopping.QuantityFormat.Describe"/> and nothing more: receipt-imported products
/// rarely have it set, and where a human has set one it can mislead — "each" or "ct" beside quantities
/// [6, 6, 6] would take the median path and charge six for cooking one, the exact bug the counted
/// branch exists to prevent. Fractionality answers correctly in both cases.</para>
/// </summary>
public static class TypicalPackage
{
    /// <summary>One package of this product: 1 when the purchases read as counts, the median when they
    /// read as a measure.</summary>
    /// <param name="purchaseQuantities">Each PURCHASE's own quantity — deliberately NOT v3.5's
    /// trip-summed buy median, which answers a different question. Two packs of beef in one trip is
    /// 2.48 lb of <em>shopping</em> and 1.24 lb of <em>package</em>, and a decrement is about the
    /// package. Don't reuse the estimator's median here.</param>
    public static decimal Of(IEnumerable<decimal> purchaseQuantities)
    {
        // Median rather than mode: continuous weights rarely repeat exactly (1.24 vs 1.26 is the same
        // pack in practice), so "most common" isn't well defined for them, and the app is median-based
        // throughout. Non-positive quantities are noise, not packages.
        var sorted = purchaseQuantities.Where(q => q > 0).OrderBy(q => q).ToList();
        if (sorted.Count == 0) return 1m;

        var mid = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;

        // A whole median means these are counts, and one of a counted thing is 1 — a receipt line reading
        // "Beef Chuck Roast × 6" is one purchase OF six, not one purchase of a six-pack, so a household
        // that habitually buys six at a time would otherwise lose all six to cooking one meal: it empties
        // the count, which LIFTS §13.5's suppression and puts the item straight back on the grocery list.
        // The median (not the mean, and not any single value) decides, so one odd corrected quantity can't
        // flip a counted product into weight mode.
        //
        // Residual limit worth stating: a weight item whose median lands on a whole number — beef at
        // exactly 2.00 lb every time — reads as counted and deducts 1. Continuous weights essentially
        // never do that, and the alternative is trusting a unit field nothing writes.
        return median == decimal.Truncate(median) ? 1m : median;
    }
}
