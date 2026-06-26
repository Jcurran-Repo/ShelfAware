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
}
