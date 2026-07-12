using ShelfAware.Core.Domain;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

public class PriceSeriesTests
{
    private static DateOnly D(int day) => new(2026, 6, day);

    [Fact]
    public void The_limes_case_charts_each_prices_and_excludes_the_bag()
    {
        // The real bug: 4 loose limes at $0.25 followed by an $8.00 bag read as a 3,100% price jump.
        // The bag is a different size bucket, so the dominant (each) series never contains it —
        // and how MANY loose limes were bought can't matter, because quantity isn't even an input.
        var points = new List<PricePoint>
        {
            new(null, D(1), 0.25m),      // 4 loose limes — qty lives on the purchase, not here
            new("each", D(10), 0.28m),   // 7 loose limes, spelled differently by extraction
            new("2 lb bag", D(20), 8.00m),
        };

        var series = PriceSeries.Dominant(points)!;

        Assert.Equal(SizeBucket.EachKey, series.SizeKey);
        Assert.Equal(new[] { 0.25m, 0.28m }, series.Points.Select(p => p.UnitPrice));
        Assert.Equal(2, series.BucketCount); // mixed sizes → the UI labels the charted bucket
    }

    [Fact]
    public void Dominant_bucket_is_the_most_bought_size()
    {
        var points = new List<PricePoint>
        {
            new("1 gal", D(1), 3.49m),
            new("1 gal", D(8), 3.59m),
            new("64 fl oz", D(15), 2.29m),
        };

        var series = PriceSeries.Dominant(points)!;

        Assert.Equal("1 gal", series.SizeKey);
        Assert.Equal(new[] { 3.49m, 3.59m }, series.Points.Select(p => p.UnitPrice));
    }

    [Fact]
    public void Dominant_tie_goes_to_the_most_recently_seen_size()
    {
        // One buy each of two sizes: chart what the user bought LAST — it's what they'd buy next.
        var points = new List<PricePoint>
        {
            new("64 fl oz", D(1), 2.29m),
            new("1 gal", D(20), 3.49m),
        };

        Assert.Equal("1 gal", PriceSeries.Dominant(points)!.SizeKey);
    }

    [Fact]
    public void Points_come_back_oldest_first_for_charting()
    {
        var points = new List<PricePoint>
        {
            new(null, D(20), 0.30m),
            new(null, D(5), 0.25m),
            new(null, null, 0.20m), // dateless (no receipt date) sorts first, like the pages treat it
        };

        Assert.Equal(new[] { 0.20m, 0.25m, 0.30m },
            PriceSeries.Dominant(points)!.Points.Select(p => p.UnitPrice));
    }

    [Fact]
    public void Single_size_products_report_one_bucket_so_no_label_is_shown()
    {
        var series = PriceSeries.Dominant([new("12 oz", D(1), 4.99m), new("12 OZ ", D(9), 5.19m)])!;

        Assert.Equal(1, series.BucketCount);
        Assert.Equal(2, series.Points.Count);
    }

    [Fact]
    public void Empty_input_returns_null()
    {
        Assert.Null(PriceSeries.Dominant([]));
    }
}
