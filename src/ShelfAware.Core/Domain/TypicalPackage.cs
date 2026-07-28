namespace ShelfAware.Core.Domain;

/// <summary>
/// How much "one package" of a product is, for the decrements that can't know (DESIGN.md §13.3).
/// <para>Cooking a recipe takes one package off the count. For a counted item that's the number 1. For a
/// WEIGHT item it can't be: deducting 1 lb would be arbitrary — a pound is not a unit of anything about
/// how this household buys — so it's the median of that product's per-purchase quantities, and a
/// household whose ground beef arrives in 1.24 lb packs deducts 1.24.</para>
/// </summary>
public static class TypicalPackage
{
    /// <summary>The median per-purchase quantity, or 1 when there's no history to take one from.</summary>
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
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }
}
