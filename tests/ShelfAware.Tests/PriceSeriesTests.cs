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

    [Fact]
    public void A_tie_in_bucket_count_goes_to_the_most_recently_seen_size()
    {
        // Two buckets, two points each — a count tie. The tie-break is the bucket with the LATEST point
        // (Max date), not the earliest: gallon's day-30 point beats 64 oz's day-20 one.
        var points = new List<PricePoint>
        {
            new("1 gal", D(1), 3.00m), new("1 gal", D(30), 3.50m),
            new("64 oz", D(10), 2.00m), new("64 oz", D(20), 2.20m),
        };

        Assert.Equal(SizeBucket.Key("1 gal"), PriceSeries.Dominant(points)!.SizeKey);
    }

    [Fact]
    public void One_receipts_two_lines_are_one_trip_at_their_average_not_a_price_move()
    {
        // The real Dentastix bug: one shopping trip listed the same 16 oz product on TWO lines ($36.19
        // and $48.26). Trends read those as consecutive purchases and showed a phantom ▲33%, while the
        // detail page — which averaged the receipt — showed no change. A trip is a trip: same size, same
        // day collapses to one point (their average), so there is nothing for a trend to compare.
        var points = new List<PricePoint>
        {
            new("16 oz", D(22), 36.19m),
            new("16 oz", D(22), 48.26m),
        };

        var series = PriceSeries.Dominant(points)!;

        var only = Assert.Single(series.Points);
        Assert.Equal(42.225m, only.UnitPrice); // (36.19 + 48.26) / 2 — not 84.45, 36.19, or 48.26
    }

    [Fact]
    public void A_zero_priced_line_is_not_a_price_and_never_joins_the_trip_average()
    {
        // The Dog Pads shape: one trip, three 50 ct lines — a $0.00 coupon/void/misread plus $38.61 and
        // $38.60. The $0 is not the item's price, so it's dropped; the trip reads the average of the two
        // real lines ($38.605), never (0 + 38.61 + 38.60) / 3 = $25.74.
        var points = new List<PricePoint>
        {
            new("50 ct", D(22), 0m),
            new("50 ct", D(22), 38.61m),
            new("50 ct", D(22), 38.60m),
        };

        var series = PriceSeries.Dominant(points)!;

        var only = Assert.Single(series.Points);
        Assert.Equal(38.605m, only.UnitPrice);
    }

    [Fact]
    public void A_negative_price_is_dropped_too_and_an_all_nonpositive_product_has_no_series()
    {
        Assert.Equal(5.00m, PriceSeries.Dominant([new("box", D(1), -1m), new("box", D(2), 5.00m)])!.Points.Single().UnitPrice);
        Assert.Null(PriceSeries.Dominant([new("box", D(1), 0m), new("box", D(2), -2m)]));
    }

    [Fact]
    public void The_dominant_bucket_is_the_one_with_the_most_TRIPS_not_the_most_lines()
    {
        // A duplicated line must not inflate a size into dominance. The "12 oz" bucket has THREE lines
        // but on ONE trip (a triple-listed receipt); the "each" bucket has TWO lines on TWO trips. Trips
        // win: two real buying occasions beat one occasion that happened to print three times.
        var points = new List<PricePoint>
        {
            new("12 oz", D(5), 4.00m), new("12 oz", D(5), 4.00m), new("12 oz", D(5), 4.00m),
            new(null, D(1), 0.25m), new(null, D(10), 0.28m),
        };

        var series = PriceSeries.Dominant(points)!;

        Assert.Equal(SizeBucket.EachKey, series.SizeKey);
        Assert.Equal(new[] { 0.25m, 0.28m }, series.Points.Select(p => p.UnitPrice));
    }

    [Fact]
    public void A_real_change_across_two_trips_survives_as_two_points()
    {
        // The collapse must not flatten a genuine trend: two DIFFERENT days is two trips, so an actual
        // price move (a real increase) is still reported.
        var points = new List<PricePoint>
        {
            new("16 oz", D(1), 3.00m),
            new("16 oz", D(20), 3.60m),
        };

        Assert.Equal(new[] { 3.00m, 3.60m }, PriceSeries.Dominant(points)!.Points.Select(p => p.UnitPrice));
    }
}
