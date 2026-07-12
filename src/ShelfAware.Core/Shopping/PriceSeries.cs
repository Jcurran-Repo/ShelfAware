using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Shopping;

/// <summary>One receipt-line price observation for a product: the size it was sold as, when, and the
/// unit price. Quantity is deliberately absent — buying 3 loose limes or 7 loose limes is the same
/// unit price, so how many were bought can never split a price series.</summary>
public record PricePoint(string? Size, DateOnly? Date, decimal UnitPrice);

/// <summary>The dominant size bucket's price observations, oldest first, plus how many distinct
/// buckets the product has (so a UI can label the series only when there's actually a mix).</summary>
public record DominantSeries(string SizeKey, IReadOnlyList<PricePoint> Points, int BucketCount);

/// <summary>
/// Comparable price series for a product. A raw sequence of unit prices isn't a trend when the sizes
/// differ — $0.25/lime followed by $8.00/bag-of-limes reads as a 3,100% "increase". The app's
/// deliberate no-unit-arithmetic stance (§ data model) means we never convert between sizes; instead,
/// mirror the predictor's dominant-size philosophy: compare like with like, within one size bucket
/// (<see cref="SizeBucket"/>, shared with the predictor and the price index).
/// </summary>
public static class PriceSeries
{
    /// <summary>The dominant (most observations; ties → most recently seen) size bucket's points,
    /// oldest first — one bucket, one honest trend. Returns null when there are no points.</summary>
    public static DominantSeries? Dominant(IReadOnlyCollection<PricePoint> points)
    {
        if (points.Count == 0) return null;
        var groups = points.GroupBy(p => SizeBucket.Key(p.Size)).ToList();
        var dominant = groups
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(p => p.Date ?? DateOnly.MinValue))
            .First();
        return new DominantSeries(
            dominant.Key,
            dominant.OrderBy(p => p.Date ?? DateOnly.MinValue).ToList(),
            groups.Count);
    }
}
