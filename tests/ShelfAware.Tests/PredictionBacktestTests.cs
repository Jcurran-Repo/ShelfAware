using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;

namespace ShelfAware.Tests;

public class PredictionBacktestTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 1);

    private static DateOnly D(int offset) => Day0.AddDays(offset);

    private static Product ProductWith(int id, string name, params int[] purchaseDays) => new()
    {
        Id = id,
        Name = name,
        Purchases = purchaseDays.Select(d => new PurchaseEvent { ProductId = id, PurchasedAt = D(d) }).ToList(),
    };

    [Fact]
    public void MetronomicHistory_ScoresZeroError_And_FullHitRate()
    {
        // Bought every 10 days, five times. Walk-forward: trips 3–5 are each predicted from the trips
        // before them, and each prediction lands exactly on the real repurchase.
        var summary = PredictionBacktest.Run([ProductWith(1, "Milk", 0, 10, 20, 30, 40)]);

        Assert.Equal(1, summary.Products);
        Assert.Equal(3, summary.Samples);
        Assert.Equal(0, summary.MedianAbsErrorDays);
        Assert.Equal(1.0, summary.HitRate);
    }

    [Fact]
    public void ADriftingTrip_ShowsUpAsError()
    {
        // 0, 10, 20 predict fine; the 4th trip lands on day 35 against a projected day 30 → |error| 5.
        var summary = PredictionBacktest.Run([ProductWith(1, "Coffee", 0, 10, 20, 35)]);

        Assert.Equal(2, summary.Samples);          // trips 3 and 4 are scoreable
        Assert.Equal(1, summary.WithinTwoDays);    // trip 3 hit; trip 4 missed by 5
        Assert.Equal(2.5, summary.MedianAbsErrorDays); // median of {0, 5}
        var product = Assert.Single(summary.PerProduct);
        Assert.Equal(0.5, product.HitRate);
    }

    [Fact]
    public void ProductsWithFewerThanThreeDates_AreSkipped()
    {
        var summary = PredictionBacktest.Run([
            ProductWith(1, "New Thing", 0, 10),
            ProductWith(2, "Newer Thing", 5),
        ]);

        Assert.Equal(0, summary.Products);
        Assert.Equal(0, summary.Samples);
        Assert.Equal(0, summary.HitRate);
    }

    [Fact]
    public void SnapshotsRespectSignals_OnlyThoseBeforeTheTrip()
    {
        // Bought day 0 and 10, restocked ("found one") day 15, repurchased day 25. Predicting trip 3
        // must see the restock (it re-anchors the projection: 15 + 10 = 25 → exact hit). No peeking at
        // anything on/after the trip being scored.
        var product = ProductWith(1, "Dog Treats", 0, 10, 25);
        product.Signals.Add(new InventorySignal
        {
            ProductId = 1,
            Kind = SignalKind.Restocked,
            SignaledAt = new DateTimeOffset(D(15).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        });

        var summary = PredictionBacktest.Run([product]);

        Assert.Equal(1, summary.Samples);
        Assert.Equal(0, summary.MedianAbsErrorDays);
    }

    [Fact]
    public void AggregatesAcrossProducts()
    {
        var summary = PredictionBacktest.Run([
            ProductWith(1, "Milk", 0, 10, 20, 30),   // 2 samples, both exact
            ProductWith(2, "Coffee", 0, 10, 20, 35), // 2 samples: one exact, one off by 5
        ]);

        Assert.Equal(2, summary.Products);
        Assert.Equal(4, summary.Samples);
        Assert.Equal(3, summary.WithinTwoDays);
        Assert.Equal(0.75, summary.HitRate);
        Assert.Equal(0, summary.MedianAbsErrorDays); // {0,0,0,5} → 0
    }
}
