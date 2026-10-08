using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Shopping;

/// <summary>One receipt-line price observation for a product: the size it was sold as, when, and the
/// unit price. Quantity is deliberately absent — buying 3 loose limes or 7 loose limes is the same
/// unit price, so how many were bought can never split a price series.</summary>
public record PricePoint(string? Size, DateOnly? Date, decimal UnitPrice);

/// <summary>One size bucket's price observations, one per shopping trip, oldest first. The key is the
/// bucket's <see cref="SizeBucket"/> key — the only size text a surface ever displays for a series.</summary>
public record SizeSeries(string SizeKey, IReadOnlyList<PricePoint> Points);

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
    /// <summary>Every size bucket's points, one per shopping trip, oldest first — one series per size,
    /// ranked most-bought first (most TRIPS; ties → most recently seen). Empty when there are no
    /// positively-priced points. This is the ONE definition of "a product's price series by size":
    /// Trends charts every entry, and <see cref="Dominant"/> is its first entry, so a surface that
    /// charts one size and a surface that charts them all agree by construction, not coincidence.
    /// <para>Two rules turn raw line observations into a comparable trend, and both live HERE so every
    /// surface that charts or compares prices (Trends, Product Detail, Reports) answers "how is this
    /// item's price moving?" the same way — a duplicated line must never read as an increase on one
    /// screen while the screen beside it, which averages the receipt, shows none:</para>
    /// <para>1. A price of zero or less is not the item's price — a $0.00 line is a coupon, void, or
    /// misread — so it is neither a trend point nor part of a trip's average. (Spend still counts a $0
    /// line where it is spent; a PRICE trend does not.)</para>
    /// <para>2. A trend point is a shopping TRIP, not a receipt line. Two lines of the same product and
    /// size on one receipt are one purchase split across lines — a multi-quantity buy, two produce
    /// weigh-ins, or a pre-quantity-fix duplicate — never an intra-trip move. Same size + same day
    /// collapse to their average before buckets are ranked or prices compared, and the ranking is by
    /// TRIP count (not line count) so duplicate lines can't inflate a bucket into dominance.</para></summary>
    public static IReadOnlyList<SizeSeries> BySize(IReadOnlyCollection<PricePoint> points) =>
        points
            .Where(p => p.UnitPrice > 0)
            .GroupBy(p => SizeBucket.Key(p.Size))
            .Select(bucket => new SizeSeries(
                bucket.Key,
                // One point per day = one point per trip (same size + day is the same shopping
                // occasion). The bucket key is the point's size — every point in a bucket already
                // shares it, and only SizeSeries.SizeKey is ever displayed.
                bucket
                    .GroupBy(p => p.Date)
                    .Select(day => new PricePoint(bucket.Key, day.Key, day.Average(p => p.UnitPrice)))
                    .OrderBy(p => p.Date ?? DateOnly.MinValue)
                    .ToList()))
            .OrderByDescending(s => s.Points.Count)
            .ThenByDescending(s => s.Points.Max(p => p.Date ?? DateOnly.MinValue))
            .ToList();

    /// <summary>The dominant (most-bought) size bucket's series — the first entry of
    /// <see cref="BySize"/> — with the bucket count beside it. Returns null when there are no
    /// positively-priced points. Surfaces that chart ONE honest trend per product (Product Detail's
    /// chart, Reports' unit-price metric) read this; they can never disagree with a surface charting
    /// every size, because this IS that list's head.</summary>
    public static DominantSeries? Dominant(IReadOnlyCollection<PricePoint> points)
    {
        var bySize = BySize(points);
        if (bySize.Count == 0) return null;
        var dominant = bySize[0];
        return new DominantSeries(dominant.SizeKey, dominant.Points, bySize.Count);
    }
}
