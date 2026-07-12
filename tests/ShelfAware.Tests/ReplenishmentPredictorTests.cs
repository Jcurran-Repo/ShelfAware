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
    public void OutNow_SetsDueDateToTheOutageDate_NotTheStatisticalFuture()
    {
        // Bought day 0 and 20 (~20-day cadence) → stats alone would put the due date around day 40.
        // Marking it out on day 25 means it's out NOW: the due date becomes the outage date, so the UI
        // reads overdue/now instead of the contradictory "due in ~15 days" next to an Overdue chip.
        var product = ProductWith([D(0), D(20)], [Signal(SignalKind.OutNow, D(25))]);

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.True(r.Pinned);
        Assert.Equal(D(25), r.DueDate); // the outage date, not the statistical ~D(40)
    }

    [Fact]
    public void OutNow_WithOnePurchase_IsOutNow_ButDoesNotFabricateCadence()
    {
        // Marking a once-bought item out makes it "out now" (Overdue, due = the outage date), but we
        // don't invent a cadence from the single purchase→outage gap — cadence stays "still learning".
        var product = ProductWith([D(0)], [Signal(SignalKind.OutNow, D(12))]);

        var r = ReplenishmentPredictor.Predict(product, D(12));

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.Equal(D(12), r.DueDate);
        Assert.Null(r.MedianIntervalDays); // no cadence fabricated from one purchase
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
    public void Restocked_DoesNotFabricateCadence_OnlyRealBuysCount()
    {
        // A single purchase + a "found one" restock is NOT two buys — a restock feeds neither rhythm, so
        // there's still no cadence to predict from. (Under the old model this wrongly read as a 10-day
        // cadence; Jordan's rule: count it only if I bought one, not if I found one.)
        var product = ProductWith([D(0)], [Signal(SignalKind.Restocked, D(10))]);

        var r = ReplenishmentPredictor.Predict(product, D(12));

        Assert.Equal(PredictionStatus.Unknown, r.Status);
        Assert.Null(r.MedianIntervalDays);
        Assert.Null(r.RebuyIntervalDays);
    }

    // --- Two-stream cadence: burn rate (purchase→outage) vs rebuy rhythm ----

    [Fact]
    public void BurnRate_DrivesThePrediction_WhenOutageCyclesExist()
    {
        // Bought every ~30 days, but runs out ~10 days after each buy (chronically under-stocked). Two
        // completed purchase→outage cycles → burn rate drives, predicting run-out from the last purchase.
        var product = ProductWith(
            [D(0), D(30), D(60)],
            [Signal(SignalKind.OutNow, D(10)), Signal(SignalKind.OutNow, D(40))]);

        var r = ReplenishmentPredictor.Predict(product, D(65));

        Assert.Equal(10, r.BurnRateDays);        // cycles {10, 10} → median 10
        Assert.Equal(30, r.RebuyIntervalDays);   // purchase gaps {30, 30}
        Assert.Equal(10, r.MedianIntervalDays);  // burn rate is the winning number
        Assert.Equal(D(70), r.DueDate);          // last purchase D(60) + 10
    }

    [Fact]
    public void OneOutageCycle_IsNotEnough_RebuyStillDrives()
    {
        // A single purchase→outage cycle can't form a median → fall back to the rebuy rhythm.
        var product = ProductWith(
            [D(0), D(30)],
            [Signal(SignalKind.OutNow, D(10))]);

        var r = ReplenishmentPredictor.Predict(product, D(35));

        Assert.Null(r.BurnRateDays);
        Assert.Equal(30, r.RebuyIntervalDays);
        Assert.Equal(30, r.MedianIntervalDays); // rebuy drives
    }

    [Fact]
    public void RunsOutEarly_WhenBurnRateIsShorterThanRebuyRhythm()
    {
        // Bought ~every 30 days but each one lasts only ~10 (two completed outage cycles) → you're out ~20
        // days every cycle. The dashboard surfaces this as "you keep running out of these".
        var product = ProductWith(
            [D(0), D(30), D(60)],
            [Signal(SignalKind.OutNow, D(10)), Signal(SignalKind.OutNow, D(40))]);

        var r = ReplenishmentPredictor.Predict(product, D(65));

        Assert.Equal(20, r.RebuyBurnGapDays); // rebuy 30 − burn 10
        Assert.True(r.RunsOutEarly);
    }

    [Fact]
    public void RunsOutEarly_IsFalse_WithoutTwoStreamData()
    {
        // Comfortably ahead: bought ~every 10 days, never marked out → no burn rate, so no gap to nag about.
        var r = ReplenishmentPredictor.Predict(ProductWith([D(0), D(10), D(20)]), D(25));

        Assert.Null(r.RebuyBurnGapDays);
        Assert.False(r.RunsOutEarly);
    }

    [Fact]
    public void SameDayTie_PurchaseWins_OutNowSignalIsCleared()
    {
        // A signal dated the SAME day as the last purchase is not active — purchases carry no time of
        // day, so the tie is ambiguous, and the purchase must win or the primary flow breaks: an item
        // pinned Overdue is bought via [Bought today], and that purchase has to clear the pin even
        // though the OutNow was signaled earlier the same day.
        var product = ProductWith([D(0), D(20)], [Signal(SignalKind.OutNow, D(20))]);

        var r = ReplenishmentPredictor.Predict(product, D(21));

        Assert.False(r.Pinned);
        Assert.NotEqual(PredictionStatus.Overdue, r.Status);
        Assert.Null(r.SignalNote);
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

    // --- Interval spread: the DueSoon window earns its width from real variance ----

    [Fact]
    public void NoisyCadence_WidensTheDueSoonWindow_ByTheIqr()
    {
        // Gaps {10, 20, 12} → median 12, IQR (median-of-halves) = 20 − 10 = 10. The flat rule would give
        // max(3, 20% of 12) = 3; the noisy rhythm earns a 10-day window instead. Due = D(42)+12 = D(54),
        // so DueSoon starts at D(44).
        var product = ProductWith([D(0), D(10), D(30), D(42)]);

        var r = ReplenishmentPredictor.Predict(product, D(44));

        Assert.Equal(10, r.IntervalSpreadDays);
        Assert.Equal(PredictionStatus.DueSoon, r.Status);
        Assert.Equal(PredictionStatus.Stocked, ReplenishmentPredictor.Predict(product, D(43)).Status);
    }

    [Fact]
    public void MetronomicCadence_KeepsTheTightWindow()
    {
        // Gaps {10, 10, 10} → IQR 0 → window stays the flat max(3, 20%) = 3. Due = D(40); DueSoon from D(37).
        var product = ProductWith([D(0), D(10), D(20), D(30)]);

        var r = ReplenishmentPredictor.Predict(product, D(36));

        Assert.Equal(0, r.IntervalSpreadDays);
        Assert.Equal(PredictionStatus.Stocked, r.Status);
        Assert.Equal(PredictionStatus.DueSoon, ReplenishmentPredictor.Predict(product, D(37)).Status);
    }

    [Fact]
    public void TwoPurchases_HaveNoSpread()
    {
        var r = ReplenishmentPredictor.Predict(ProductWith([D(0), D(20)]), D(21));

        Assert.Null(r.IntervalSpreadDays); // one gap — a spread would be noise
    }

    // --- Short cadence: the DueSoon window can't span the whole cycle -----------

    [Fact]
    public void ShortCadence_IsStockedTheDayItsRestocked_NotImmediatelyDueSoon()
    {
        // A milk-style item bought ~every 3 days. The flat 3-day DueSoon floor is as wide as the whole
        // cycle, so before the window clamp a fresh buy re-anchored the due date but landed right back
        // inside the warning window — the item read "due soon" the instant you bought it and could never
        // leave Running Low, making [Bought today] / [Restocked] look broken. A fresh stock-back must earn
        // at least one Stocked day. (Gaps {3,3} → median 3, due = D(6)+3 = D(9), window clamped to 2 days.)
        var product = ProductWith([D(0), D(3), D(6)]);

        Assert.Equal(3, ReplenishmentPredictor.Predict(product, D(6)).MedianIntervalDays);
        Assert.Equal(PredictionStatus.Stocked, ReplenishmentPredictor.Predict(product, D(6)).Status); // buy day
        Assert.Equal(PredictionStatus.DueSoon, ReplenishmentPredictor.Predict(product, D(7)).Status); // window opens
        Assert.Equal(PredictionStatus.DueSoon, ReplenishmentPredictor.Predict(product, D(9)).Status); // due, not yet overdue
        Assert.Equal(PredictionStatus.Overdue, ReplenishmentPredictor.Predict(product, D(10)).Status);
    }

    // --- Stock-up factor: a big buy pushes the due date out --------------------

    private static Product ProductWithQuantities(params (int Day, decimal Qty)[] purchases) => new()
    {
        Id = 1,
        Name = "Test",
        Purchases = purchases.Select(p => new PurchaseEvent { ProductId = 1, PurchasedAt = D(p.Day), Quantity = p.Qty }).ToList(),
    };

    [Fact]
    public void StockUp_ExtendsTheDueDate_ByTheQuantityRatio()
    {
        // Usually 1 bag every ~10 days; the last trip bought 3 → the projection stretches to ~30 days.
        var product = ProductWithQuantities((0, 1), (10, 1), (20, 3));

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.Equal(3.0, r.StockUpFactor);
        Assert.Equal(D(50), r.DueDate); // D(20) + floor(10 × 3)
    }

    [Fact]
    public void StockUpFactor_IsCappedAtThree()
    {
        var product = ProductWithQuantities((0, 1), (10, 1), (20, 12));

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.Equal(3.0, r.StockUpFactor);
        Assert.Equal(D(50), r.DueDate); // capped: not D(20) + 120
    }

    [Fact]
    public void SmallerThanUsualBuy_DoesNotShortenTheDueDate()
    {
        // Extend-only: quantities are noisy (weights, partial packs) — a small last buy keeps the
        // normal cadence rather than nagging early.
        var product = ProductWithQuantities((0, 2), (10, 2), (20, 1));

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.Null(r.StockUpFactor);
        Assert.Equal(D(30), r.DueDate); // D(20) + the unscaled median 10
    }

    [Fact]
    public void RestockAnchor_IgnoresTheQuantityFactor()
    {
        // The last stock-back is a restock ("found one"), not a purchase — there's no quantity to
        // scale by, so the plain median projects from the restock date.
        var product = ProductWithQuantities((0, 1), (10, 3));
        product.Signals.Add(Signal(SignalKind.Restocked, D(15)));

        var r = ReplenishmentPredictor.Predict(product, D(16));

        Assert.Null(r.StockUpFactor);
        Assert.Equal(D(25), r.DueDate); // D(15) + median 10, unscaled
    }

    [Fact]
    public void SameDayQuantities_SumIntoOneTrip()
    {
        // Two 1-unit rows on the same date are one 2-unit trip — same-day collapse applies to
        // quantities too, so a split receipt line doesn't read as a stock-up against itself.
        var product = ProductWithQuantities((0, 1), (0, 1), (10, 1), (10, 1), (20, 2));

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.Null(r.StockUpFactor); // every trip totals 2 — nothing unusual about the last one
        Assert.Equal(D(30), r.DueDate);
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
    public void EachFamilySpellings_FoldIntoOneCadence()
    {
        // Loose produce bought weekly, but extraction alternates the size between null and "Each".
        // Raw-text grouping would learn the cadence from every-other purchase (14-day gaps) and warn
        // a week late, silently. The each-family is ONE size bucket, so all five buys feed the rhythm.
        var product = new Product
        {
            Id = 1,
            Name = "Limes",
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0),  Size = null },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(7),  Size = "Each" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(14), Size = null },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(21), Size = "Each" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(28), Size = null },
            ],
        };

        var r = ReplenishmentPredictor.Predict(product, D(30));

        Assert.Equal(7, r.MedianIntervalDays);   // all five dates, not the same-spelling subset
        Assert.Equal(D(35), r.DueDate);          // last buy D(28) + 7
        Assert.Equal("Each", r.RecommendedSize); // the spelled form survives as the hint
    }

    [Fact]
    public void EachFamily_DominatesOverABag_WhenBoughtMoreOften()
    {
        // Two loose buys (spelled null and "each") plus one bag: loose is the dominant FORM (2 of 3),
        // so the cadence comes from the two loose dates — the bag stays a separate size, exactly like
        // the price series treats it. Raw-text grouping saw three one-buy groups and blended them all.
        var product = new Product
        {
            Id = 1,
            Name = "Envy Apples",
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0),  Size = "3 lb" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(10), Size = null },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(24), Size = "each" },
            ],
        };

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.Equal(14, r.MedianIntervalDays);  // the loose-to-loose gap, no bag date blended in
        Assert.Equal(D(38), r.DueDate);          // last loose buy D(24) + 14
        Assert.Equal("each", r.RecommendedSize);
    }

    [Fact]
    public void RecommendedSize_PrefersTheMostRecentSpelledForm_OverANull()
    {
        // The most recent buy has no size text, but an older buy in the same (each) bucket says
        // "Each" — keep the spelled hint rather than silently dropping it. All-null stays null.
        var spelled = new Product
        {
            Id = 1,
            Name = "Avocados",
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0),  Size = "Each" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(12), Size = null },
            ],
        };
        var allNull = new Product
        {
            Id = 2,
            Name = "Cucumbers",
            Purchases =
            [
                new PurchaseEvent { ProductId = 2, PurchasedAt = D(0),  Size = null },
                new PurchaseEvent { ProductId = 2, PurchasedAt = D(12), Size = null },
            ],
        };

        Assert.Equal("Each", ReplenishmentPredictor.Predict(spelled, D(15)).RecommendedSize);
        Assert.Null(ReplenishmentPredictor.Predict(allNull, D(15)).RecommendedSize);
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
