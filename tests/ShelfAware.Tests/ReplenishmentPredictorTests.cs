using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;

namespace ShelfAware.Tests;

public class ReplenishmentPredictorTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 1);

    private static DateOnly D(int dayOffset) => Day0.AddDays(dayOffset);

    private static Product ProductWith(IEnumerable<DateOnly>? purchases = null, IEnumerable<InventorySignal>? signals = null) =>
        new()
        {
            Id = 1,
            Name = "Test",
            Purchases = (purchases ?? []).Select(d => new PurchaseEvent { ProductId = 1, PurchasedAt = d }).ToList(),
            Signals = (signals ?? []).ToList()
        };

    private static InventorySignal Signal(SignalKind kind, DateOnly on) =>
        new() { ProductId = 1, Kind = kind, SignaledAt = new DateTimeOffset(on.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) };

    // --- §6.2: < 2 events → Unknown -----------------------------------------

    [Fact]
    public void NoPurchases_IsUnknown()
    {
        var r = ReplenishmentPredictor.Predict(ProductWith(), D(10));

        Assert.Equal(PredictionStatus.Unknown, r.Status);
        Assert.Null(r.DueDate);
        Assert.Null(r.MedianIntervalDays);
    }

    [Fact]
    public void SinglePurchase_IsUnknown()
    {
        var r = ReplenishmentPredictor.Predict(ProductWith([D(0)]), D(10));

        Assert.Equal(PredictionStatus.Unknown, r.Status);
        Assert.Null(r.DueDate);
    }

    [Fact]
    public void TwoPurchases_ComputeMedianAndDueDate()
    {
        var r = ReplenishmentPredictor.Predict(ProductWith([D(0), D(10)]), D(12));

        Assert.Equal(10, r.MedianIntervalDays);
        Assert.Equal(D(20), r.DueDate);
        Assert.Equal(PredictionStatus.Stocked, r.Status);
    }

    [Fact]
    public void DueDate_FloorsTheInterval_ErringEarly()
    {
        // Gaps 10 and 13 → median 11.5. The due date floors the interval to 11 days, landing a day
        // early rather than a day late — the "stay ahead" bias. (MedianIntervalDays keeps the raw 11.5.)
        var r = ReplenishmentPredictor.Predict(ProductWith([D(0), D(10), D(23)]), D(25));

        Assert.Equal(11.5, r.MedianIntervalDays);
        Assert.Equal(D(34), r.DueDate); // D(23) + floor(11.5) = +11, not the rounded +12
    }

    // --- §6.3: median is robust; ≥4 events trim outliers --------------------

    [Fact]
    public void Median_IgnoresVacationOutlier_WithoutTrim()
    {
        // Gaps 10, 10, 100 → median of 3 values is the middle one, 10 (the outlier never moves it).
        var r = ReplenishmentPredictor.Predict(ProductWith([D(0), D(10), D(20), D(120)]), D(120));

        Assert.Equal(10, r.MedianIntervalDays);
    }

    [Fact]
    public void Trim_DropsIntervalsOverThreeXMedian_BeforeRecomputing()
    {
        // 5 events → gaps 10, 12, 14, 100. Untrimmed median = (12+14)/2 = 13.
        // 3× threshold = 39 drops the 100; median of {10,12,14} = 12.
        var r = ReplenishmentPredictor.Predict(
            ProductWith([D(0), D(10), D(22), D(36), D(136)]), D(136));

        Assert.Equal(12, r.MedianIntervalDays);
    }

    // --- §6.5: status boundaries (±1 day around each edge) ------------------
    // Two purchases 20 days apart → median 20, DueDate = D(40),
    // threshold = max(3, 0.2*20) = 4 → DueSoon window starts at D(36).

    [Theory]
    [InlineData(35, PredictionStatus.Stocked)]   // one day before the DueSoon window
    [InlineData(36, PredictionStatus.DueSoon)]   // first day of the DueSoon window
    [InlineData(40, PredictionStatus.DueSoon)]   // exactly the due date (not yet Overdue)
    [InlineData(41, PredictionStatus.Overdue)]   // one day past the due date
    public void StatusBoundaries(int todayOffset, PredictionStatus expected)
    {
        var r = ReplenishmentPredictor.Predict(ProductWith([D(0), D(20)]), D(todayOffset));

        Assert.Equal(expected, r.Status);
    }

    // --- §6.6: signal overrides --------------------------------------------

    [Fact]
    public void OutNow_ForcesOverdueAndPins_EvenWhenStatsSayStocked()
    {
        var product = ProductWith([D(0), D(20)], [Signal(SignalKind.OutNow, D(24))]);

        var r = ReplenishmentPredictor.Predict(product, D(25)); // stats alone → Stocked

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.True(r.Pinned);
        Assert.Equal("Marked out of stock", r.SignalNote);
    }

    [Fact]
    public void RunningLow_FloorsAtDueSoon()
    {
        var product = ProductWith([D(0), D(20)], [Signal(SignalKind.RunningLow, D(24))]);

        var r = ReplenishmentPredictor.Predict(product, D(25)); // stats alone → Stocked

        Assert.Equal(PredictionStatus.DueSoon, r.Status);
        Assert.False(r.Pinned);
        Assert.Equal("Marked running low", r.SignalNote);
    }

    [Fact]
    public void RunningLow_DoesNotDowngradeOverdue()
    {
        var product = ProductWith([D(0), D(20)], [Signal(SignalKind.RunningLow, D(45))]);

        var r = ReplenishmentPredictor.Predict(product, D(50)); // stats alone → Overdue

        Assert.Equal(PredictionStatus.Overdue, r.Status);
    }

    [Fact]
    public void Restocked_CountsAsPurchaseEquivalentDate()
    {
        // One real purchase + a Restocked signal = two event dates → no longer "still learning".
        var product = ProductWith([D(0)], [Signal(SignalKind.Restocked, D(10))]);

        var r = ReplenishmentPredictor.Predict(product, D(12));

        Assert.Equal(10, r.MedianIntervalDays);
        Assert.Equal(D(20), r.DueDate);
        Assert.Equal(PredictionStatus.Stocked, r.Status);
    }

    [Fact]
    public void Restocked_ClearsAnEarlierOutNow()
    {
        var product = ProductWith([D(0), D(20)],
        [
            Signal(SignalKind.OutNow, D(22)),
            Signal(SignalKind.Restocked, D(25))
        ]);

        var r = ReplenishmentPredictor.Predict(product, D(26));

        Assert.NotEqual(PredictionStatus.Overdue, r.Status);
        Assert.False(r.Pinned);
    }

    // --- §6.1: same-day collapse -------------------------------------------

    [Fact]
    public void SameDayPurchases_CollapseToOneDate()
    {
        // Duplicate dates must not create zero-day intervals that skew the median.
        var product = ProductWith([D(0), D(0), D(10), D(10), D(20)]);

        var r = ReplenishmentPredictor.Predict(product, D(22));

        Assert.Equal(10, r.MedianIntervalDays);  // gaps {10, 10}, not {0, 10, 0, 10}
        Assert.Equal(D(30), r.DueDate);
    }

    // --- Size is metadata: the dominant size drives cadence + recommendation -----

    [Fact]
    public void DominantSize_DrivesCadenceAndRecommendation()
    {
        // Milk bought mostly as gallons (3×, every 20 days) plus a couple of random half-gallons. The
        // gallon is dominant, so the cadence comes from the gallon purchases only — not a noisy blend of
        // all five dates — and we recommend the gallon.
        var product = new Product
        {
            Id = 1,
            Name = "Whole Milk",
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0),  Size = "1 gal" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(5),  Size = "1/2 gal" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(10), Size = "1/2 gal" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(20), Size = "1 gal" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(40), Size = "1 gal" },
            ],
        };

        var r = ReplenishmentPredictor.Predict(product, D(45));

        Assert.Equal("1 gal", r.RecommendedSize);
        Assert.Equal(20, r.MedianIntervalDays);  // gallon gaps {20, 20}, not the all-sizes blend
        Assert.Equal(D(60), r.DueDate);          // last gallon D(40) + 20
    }

    [Fact]
    public void DominantSize_TieBreaksToMostRecentlyBought()
    {
        // 2 gallons and 2 half-gallons, but the most recent buy was a half-gallon → recommend that.
        var product = new Product
        {
            Id = 1,
            Name = "Whole Milk",
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0),  Size = "1 gal" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(10), Size = "1 gal" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(20), Size = "1/2 gal" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(30), Size = "1/2 gal" },
            ],
        };

        var r = ReplenishmentPredictor.Predict(product, D(35));

        Assert.Equal("1/2 gal", r.RecommendedSize);
    }

    [Fact]
    public void MixedSizes_FallBackToAllPurchases_WhenNoSizeHasEnoughHistory()
    {
        // One 10.6 oz and one 11 oz buy: no single size has 2 yet, so instead of "still learning" we
        // predict from all purchases (trivially-different sizes shouldn't strand the item). Still
        // recommend the dominant (here: most-recent) size.
        var product = new Product
        {
            Id = 1,
            Name = "Cod Skin Dog Treats",
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0),  Size = "10.6 oz" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(20), Size = "11 oz" },
            ],
        };

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.NotEqual(PredictionStatus.Unknown, r.Status);  // predicts, not "still learning"
        Assert.Equal(20, r.MedianIntervalDays);               // gap between the two buys
        Assert.Equal(D(40), r.DueDate);
        Assert.Equal("11 oz", r.RecommendedSize);
    }
}
