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
/// mirror the predictor's dominant-size philosophy: compare like with like, within one size bucket.
/// </summary>
public static class PriceSeries
{
    /// <summary>The "sold per each" family — loose produce and per-item goods. Extraction writes these
    /// inconsistently (usually null, sometimes a literal "each"/"ea"/"1 ct"), and they're all the same
    /// pricing basis, so they collapse into one bucket.</summary>
    private static readonly HashSet<string> EachSizes =
        new(StringComparer.OrdinalIgnoreCase) { "", "each", "ea", "ea.", "per each", "1 each", "1 ct", "1ct", "loose", "single" };

    public const string EachKey = "each";

    /// <summary>Grouping key for a size string: the each-family collapses to <see cref="EachKey"/>;
    /// anything else groups by its trimmed, lowercased text.</summary>
    public static string Bucket(string? size)
    {
        var s = (size ?? "").Trim();
        return EachSizes.Contains(s) ? EachKey : s.ToLowerInvariant();
    }

    /// <summary>The dominant (most observations; ties → most recently seen) size bucket's points,
    /// oldest first — one bucket, one honest trend. Returns null when there are no points.</summary>
    public static DominantSeries? Dominant(IReadOnlyCollection<PricePoint> points)
    {
        if (points.Count == 0) return null;
        var groups = points.GroupBy(p => Bucket(p.Size)).ToList();
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
