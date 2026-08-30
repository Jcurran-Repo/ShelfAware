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
    /// <summary>The dominant (most TRIPS; ties → most recently seen) size bucket's points, one per
    /// shopping trip, oldest first — one bucket, one honest trend. Returns null when there are no
    /// positively-priced points.
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
    public static DominantSeries? Dominant(IReadOnlyCollection<PricePoint> points)
    {
        var buckets = points
            .Where(p => p.UnitPrice > 0)
            .GroupBy(p => SizeBucket.Key(p.Size))
            .Select(bucket => new
            {
                bucket.Key,
                // One point per day = one point per trip (same size + day is the same shopping
                // occasion). The bucket key is the point's size — every point in a bucket already
                // shares it, and only DominantSeries.SizeKey is ever displayed.
                Trips = bucket
                    .GroupBy(p => p.Date)
                    .Select(day => new PricePoint(bucket.Key, day.Key, day.Average(p => p.UnitPrice)))
                    .OrderBy(p => p.Date ?? DateOnly.MinValue)
                    .ToList(),
            })
            .ToList();
        if (buckets.Count == 0) return null;

        var ranked = buckets
            .OrderByDescending(b => b.Trips.Count)
            .ThenByDescending(b => b.Trips.Max(p => p.Date ?? DateOnly.MinValue));
        // Stryker disable once Linq: First() → FirstOrDefault() is unobservable — the guard above means
        // buckets is non-empty, so First cannot throw. Isolated on its own line so the ordering/aggregate
        // mutants in the chain above stay mutation-tested.
        var dominant = ranked.First();
        return new DominantSeries(dominant.Key, dominant.Trips, buckets.Count);
    }
}
