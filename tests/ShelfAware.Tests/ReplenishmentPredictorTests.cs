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
    public void ASecondOutageInTheSameCycle_AddsNoBurnSample()
    {
        // One purchase can complete one cycle: the FIRST outage after it says how long that trip's worth
        // lasted; a second "still out" tap before the next purchase is the same outage restated. Cycles
        // here are {10, 15} → 12.5; counting the D(20) repeat would read {10, 20, 15} → 15.
        var product = ProductWith(
            [D(0), D(30), D(60)],
            [Signal(SignalKind.OutNow, D(10)), Signal(SignalKind.OutNow, D(20)), Signal(SignalKind.OutNow, D(45))]);

        var r = ReplenishmentPredictor.Predict(product, D(65));

        Assert.Equal(12.5, r.BurnRateDays);
        Assert.Equal(12.5, r.MedianIntervalDays); // burn (12.5) still beats rebuy (30) to drive
    }

    [Fact]
    public void AnOutageBeforeTheFirstPurchase_ClosesNoCycle()
    {
        // Pre-history: nothing was bought yet, so there is no trip for it to have burned through.
        // Real cycles are {10, 10}; letting the D(5) outage pair with anything would corrupt the median.
        var product = ProductWith(
            [D(10), D(40), D(70)],
            [Signal(SignalKind.OutNow, D(5)), Signal(SignalKind.OutNow, D(20)), Signal(SignalKind.OutNow, D(50))]);

        var r = ReplenishmentPredictor.Predict(product, D(75));

        Assert.Equal(10, r.BurnRateDays);
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
        Assert.Equal(PredictionStatus.Stocked, r.Status); // fully cleared — not merely "not Overdue"
        Assert.Null(r.SignalNote);
    }

    [Fact]
    public void SignalTodayWouldBeInert_IsTrueExactlyWhileTheStockBackIsToday()
    {
        // The member exists for surfaces whose copy invites "say you're out": an OutNow dated today
        // fails the strict > filter whenever the last stock-back is also today — and neither date
        // ever changes, so that signal stays dead for good. A page must not promise the act works;
        // the engine states the fact beside the tie rule so no surface re-derives it.
        var bought = ProductWith([D(0), D(20)]);
        Assert.True(ReplenishmentPredictor.Predict(bought, D(20)).SignalTodayWouldBeInert);
        Assert.False(ReplenishmentPredictor.Predict(bought, D(21)).SignalTodayWouldBeInert);

        // A restock is a stock-back too — the mis-tapped-Restocked road, where a same-day attempt
        // to undo the tap is exactly the advice that silently fails.
        var restocked = ProductWith([D(0), D(10)], [Signal(SignalKind.Restocked, D(20))]);
        Assert.True(ReplenishmentPredictor.Predict(restocked, D(20)).SignalTodayWouldBeInert);

        // No stock-back at all: a fresh OutNow is always active, so nothing is inert.
        Assert.False(ReplenishmentPredictor.Predict(ProductWith(), D(20)).SignalTodayWouldBeInert);

        // ⚠️ A stock-back LATER than today discards a signal filed today just the same — the filter is
        // "strictly after the stock-back", not "not the same day". Reachable: add_purchase accepts any
        // date with no future clamp, and the documented TZ gotcha can date a restock tomorrow. == here
        // called that state "not inert" while the engine discarded the signal — the exact silent no-op
        // the member exists to name, surviving one state over.
        Assert.True(ReplenishmentPredictor.Predict(ProductWith([D(0), D(25)]), D(20)).SignalTodayWouldBeInert);
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

        // The restock re-anchors the ~20-day rhythm to D(25), so the day after is squarely Stocked —
        // pinning the exact status catches a clear that leaves the item stranded in DueSoon.
        Assert.Equal(PredictionStatus.Stocked, r.Status);
        Assert.False(r.Pinned);
    }

    // --- "Still in stock": the honest cousin of Restocked — snooze, don't re-anchor a full cadence ----

    [Fact]
    public void StillInStock_SnoozesAnOverdueItem_WithoutReAnchoringAFullCadence()
    {
        // ~20-day rhythm, due D(40); today D(45) → overdue by rhythm. "Still in stock" says you never ran
        // out but have the OLD stock, so it snoozes ~a quarter of the cadence (max(4, 5) = 5 days) → D(50),
        // reads Stocked, and is NOT pinned. It must NOT re-anchor to D(65) (today + a full cadence).
        var product = ProductWith([D(0), D(20)], [Signal(SignalKind.StillInStock, D(45))]);

        var r = ReplenishmentPredictor.Predict(product, D(45));

        Assert.Equal(PredictionStatus.Stocked, r.Status);
        Assert.Equal(D(50), r.DueDate);
        Assert.Equal("You said you still had some", r.SignalNote);
        Assert.False(r.Pinned);
    }

    [Fact]
    public void StillInStock_ProjectsMuchLessThanRestocked()
    {
        // The whole point: Restocked assumes a fresh full supply (re-anchors a full cadence → D(65)),
        // "still in stock" is the old partial stock (a short snooze → D(50)). The difference is the fix.
        var restocked = ReplenishmentPredictor.Predict(
            ProductWith([D(0), D(20)], [Signal(SignalKind.Restocked, D(45))]), D(45));
        var stillHave = ReplenishmentPredictor.Predict(
            ProductWith([D(0), D(20)], [Signal(SignalKind.StillInStock, D(45))]), D(45));

        Assert.Equal(D(65), restocked.DueDate); // fresh supply → full cadence out
        Assert.Equal(D(50), stillHave.DueDate); // still-have → a modest snooze
        Assert.True(stillHave.DueDate < restocked.DueDate);
    }

    [Fact]
    public void StillInStock_ClearsAnEarlierOutNow()
    {
        // "I'm out" on D(43), then "actually I still have some" on D(45): the later statement wins, so the
        // OutNow pin is cleared and the item snoozes to Stocked rather than staying pinned Overdue.
        var product = ProductWith([D(0), D(20)],
        [
            Signal(SignalKind.OutNow, D(43)),
            Signal(SignalKind.StillInStock, D(45)),
        ]);

        var r = ReplenishmentPredictor.Predict(product, D(45));

        Assert.Equal(PredictionStatus.Stocked, r.Status);
        Assert.False(r.Pinned);
    }

    [Fact]
    public void AnOutNowAfterStillInStock_StillPins()
    {
        // The reverse order: "still have some" on D(43), then "we're out" on D(45). The most recent
        // statement is the OutNow, so it pins Overdue — recency decides, not the kind.
        var product = ProductWith([D(0), D(20)],
        [
            Signal(SignalKind.StillInStock, D(43)),
            Signal(SignalKind.OutNow, D(45)),
        ]);

        var r = ReplenishmentPredictor.Predict(product, D(45));

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.True(r.Pinned);
        Assert.Equal(D(45), r.DueDate);
    }

    [Fact]
    public void StillInStock_OnlyExtends_NeverShortensANotYetDueItem()
    {
        // ~40-day rhythm, due D(80) — comfortably Stocked today. A "still in stock" tap snoozes to
        // D(55), which is EARLIER than the rhythm's own due date, so it changes nothing: the button can
        // only ever push a due date OUT, never pull one in.
        var product = ProductWith([D(0), D(40)], [Signal(SignalKind.StillInStock, D(45))]);

        var r = ReplenishmentPredictor.Predict(product, D(45));

        Assert.Equal(PredictionStatus.Stocked, r.Status);
        Assert.Equal(D(80), r.DueDate); // the rhythm's projection, untouched
    }

    [Fact]
    public void StillInStock_DoesNotDismissAnExpiredLabel()
    {
        // "Still in stock" says you have LEFTOVERS — not that they're still good (that's the
        // Restocked-after-label override, "I froze it"). So an expired label still wins: the snooze is
        // overridden and the item stays pinned Overdue/expired. (Contrast RestockedAfterTheLabel_Overrides.)
        var product = Expiring((D(0), D(5)));
        product.Signals = [Signal(SignalKind.StillInStock, D(7))];

        var r = ReplenishmentPredictor.Predict(product, D(8), honorExpirations: true);

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.True(r.Expired);
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
    public void StockUpFactor_ScalesByTheWholeRatio_WithNoCeiling()
    {
        // The 3× ceiling was removed 2026-07-28: buy twelve when you usually buy one and you HAVE
        // twelve, so the projection stretches twelvefold. Capping it made the app ask for more while
        // nine were still in the freezer — the behaviour v4.0 exists to stop.
        var product = ProductWithQuantities((0, 1), (10, 1), (20, 12));

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.Equal(12.0, r.StockUpFactor);
        Assert.Equal(D(140), r.DueDate); // D(20) + floor(10 × 12), not the old D(50)
    }

    [Fact]
    public void AnAbsurdQuantity_CannotProjectAnItemOutOfSightOrOverflowTheDate()
    {
        // The stretch is bounded at two years — an arithmetic guard, not the 3× behavioural ceiling that
        // was removed. Quantity is only clamped upward from zero on the way in, so one misread line (a
        // price or a size read as a count) used to project an item years out and it vanished from every
        // list with nothing saying why. Bigger still used to THROW: 500,000,000 raised
        // ArgumentOutOfRangeException out of DateOnly.AddDays, taking down every page that predicts.
        var silent = ProductWithQuantities((0, 1), (10, 1), (20, 500));
        var crashing = ProductWithQuantities((0, 1), (10, 1), (20, 500_000_000));

        // 10-day rhythm × 500 would be 5,000 days; the bound holds it to 730.
        Assert.Equal(D(750), ReplenishmentPredictor.Predict(silent, D(25)).DueDate);
        Assert.Equal(D(750), ReplenishmentPredictor.Predict(crashing, D(25)).DueDate);

        // And the REPORTED factor is the one applied, not the raw ratio: Product Detail says "due date
        // pushed out to match", so a 500× claim beside a 73× stretch would be a lie on the page.
        Assert.Equal(73.0, ReplenishmentPredictor.Predict(silent, D(25)).StockUpFactor);
    }

    [Fact]
    public void TheProjectionBound_NeverShortensALegitimatelySlowRhythm()
    {
        // The floor is the unstretched median itself, so an item genuinely bought every three years
        // keeps its own rhythm — the bound only ever trims a stock-up STRETCH.
        var product = ProductWithQuantities((0, 1), (1095, 1));

        var r = ReplenishmentPredictor.Predict(product, D(1100));

        Assert.Null(r.StockUpFactor);
        Assert.Equal(D(2190), r.DueDate); // D(1095) + the full 1095-day median, not clipped to 730
    }

    [Fact]
    public void ARealHoard_IsWellInsideTheBound()
    {
        // Twelve when you usually buy one, on a 10-day rhythm: 120 days, nowhere near 730. The guard
        // must not touch the behaviour it was added alongside.
        var product = ProductWithQuantities((0, 1), (10, 1), (20, 12));

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.Equal(12.0, r.StockUpFactor);
        Assert.Equal(D(140), r.DueDate);
    }

    // ---- §13.5: a count suppresses the recommendation -------------------------------------------

    /// <summary>An overdue item (bought on a ~10-day rhythm, nothing since D(20)) that the household has
    /// also counted. Without the count it is Overdue on D(45).</summary>
    private static Product CountedAndOverdue(decimal onHand, int countedOnDay)
    {
        var product = ProductWithQuantities((0, 1), (10, 1), (20, 1));
        product.TrackQuantity = true;
        product.QuantityOnHand = onHand;
        product.QuantityCountedAt = new DateTimeOffset(D(countedOnDay).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return product;
    }

    [Fact]
    public void AFreshCount_SuppressesTheBuyRecommendation()
    {
        // Real evidence beats a learned guess: the rhythm says buy, the shelf says there are three.
        var r = ReplenishmentPredictor.Predict(CountedAndOverdue(3, countedOnDay: 44), D(45), honorQuantity: true);

        Assert.Equal(PredictionStatus.Stocked, r.Status);
        Assert.True(r.SuppressedByCount);
        Assert.False(r.CountLooksStale);
        // The due date and rhythms are UNTOUCHED, so a surface can still say when it would have asked —
        // suppression has to be visible and correctable, never a silent disappearance.
        Assert.Equal(D(30), r.DueDate);
        Assert.Equal(10, r.MedianIntervalDays);
    }

    [Fact]
    public void TheFlagIsInert_ByDefault()
    {
        // Same product, no flag: a call site that forgets it under-suppresses (recommends something you
        // may already have — annoying and visible) rather than silencing an item nobody meant to count.
        var r = ReplenishmentPredictor.Predict(CountedAndOverdue(3, countedOnDay: 44), D(45));

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.False(r.SuppressedByCount);
    }

    [Fact]
    public void ACountGoneStale_StopsSuppressingAndSaysSo()
    {
        // Three counted on D(0), one lasts ~10 days: they should have run out around D(30). On D(45) the
        // engine stops trusting the number and asks again — the answer to "an inventory decays".
        var r = ReplenishmentPredictor.Predict(CountedAndOverdue(3, countedOnDay: 0), D(45), honorQuantity: true);

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.False(r.SuppressedByCount);
        Assert.True(r.CountLooksStale);
    }

    [Fact]
    public void AZeroCount_DoesNotPin_BecauseOnlyAHumanCanReportAnOutage()
    {
        // §13.4 seen from the engine: reaching zero is a hypothesis, not evidence. It merely stops
        // suppressing — the item falls back to whatever the rhythm already said.
        var product = CountedAndOverdue(0, countedOnDay: 44);

        var r = ReplenishmentPredictor.Predict(product, D(45), honorQuantity: true);

        Assert.Equal(PredictionStatus.Overdue, r.Status); // the rhythm's verdict, not a pin
        Assert.False(r.Pinned);
        Assert.False(r.SuppressedByCount);
    }

    [Fact]
    public void AnExplicitOutNow_BeatsTheCount()
    {
        // The human said they're out while the number still says two. The person wins: a count is a
        // memory of a past look, an OutNow is a statement about now.
        var product = CountedAndOverdue(2, countedOnDay: 20);
        product.Signals.Add(Signal(SignalKind.OutNow, D(40)));

        var r = ReplenishmentPredictor.Predict(product, D(45), honorQuantity: true);

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.True(r.Pinned);
        Assert.False(r.SuppressedByCount);
    }

    [Fact]
    public void AnUncountedProduct_IsUntouchedByTheFlag()
    {
        var r = ReplenishmentPredictor.Predict(ProductWithQuantities((0, 1), (10, 1), (20, 1)), D(45), honorQuantity: true);

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.False(r.SuppressedByCount);
        Assert.False(r.CountLooksStale);
    }

    // ---- §13.5: the drift horizon is per PACKAGE, not per trip -----------------------------------

    [Fact]
    public void TheDriftHorizon_ReadsTheMedianAsOneTripsWorth_NotOnePackage()
    {
        // A household that buys SIX at a time on a ~60-day rhythm. That median is how long six last, so
        // six on hand are gone in ~60 days — not 360. Reading it as days-per-package meant the drift
        // check couldn't fire for a year, on exactly the bulk-buying households §13 was built for.
        // It's also the reading StockUpFactor already uses: a 2× buy stretches the due date 2×, which is
        // the statement "the median covers a TYPICAL TRIP'S worth".
        var product = ProductWithQuantities((0, 6), (60, 6), (120, 6));
        product.TrackQuantity = true;
        product.QuantityOnHand = 6m;
        product.QuantityCountedAt = new DateTimeOffset(D(120).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var r = ReplenishmentPredictor.Predict(product, D(200), honorQuantity: true);

        Assert.Equal(D(180), r.CountRunsOutOn); // 120 + (60 ÷ 6) × 6, not 120 + 60 × 6
        Assert.True(r.CountLooksStale);
        Assert.False(r.SuppressedByCount);
    }

    [Fact]
    public void ABulkBuyersFreshCount_IsStillBelieved()
    {
        // The other half of the same rule: shortening the horizon must not make every hoard read stale.
        // Six counted on D(120) last until D(180), and the rhythm starts asking at D(168) — so D(175) is
        // exactly the window where there IS a recommendation and the count is still good enough to hold
        // it back.
        var product = ProductWithQuantities((0, 6), (60, 6), (120, 6));
        product.TrackQuantity = true;
        product.QuantityOnHand = 6m;
        product.QuantityCountedAt = new DateTimeOffset(D(120).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var r = ReplenishmentPredictor.Predict(product, D(175), honorQuantity: true);

        Assert.True(r.SuppressedByCount);
        Assert.False(r.CountLooksStale);
    }

    [Fact]
    public void ABuyerOfOne_IsUnaffectedByThePerPackageRule()
    {
        // Where the typical trip IS one package the two readings coincide, so nothing moves for the
        // ordinary staple: three counted on D(44) at a 10-day rhythm last until D(74).
        var r = ReplenishmentPredictor.Predict(CountedAndOverdue(3, countedOnDay: 44), D(45), honorQuantity: true);

        Assert.Equal(D(74), r.CountRunsOutOn);
        Assert.True(r.SuppressedByCount);
    }

    // ---- §13.5: what a count is NOT allowed to silence -------------------------------------------

    /// <summary>Bought on a ~10-day rhythm, the last buy on D(40) carrying a best-by label of D(46).</summary>
    private static Product DatedAndCounted(decimal onHand, int countedOnDay)
    {
        var product = ProductWith([D(20), D(30), D(40)]);
        product.Purchases.Single(p => p.PurchasedAt == D(40)).ExpirationDate = D(46);
        product.TrackQuantity = true;
        product.QuantityOnHand = onHand;
        product.QuantityCountedAt = new DateTimeOffset(D(countedOnDay).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return product;
    }

    [Fact]
    public void ACount_CannotSilenceAnApproachingExpiration()
    {
        // v3.6's cap is escalate-only and has to stay that way. A count answers "how many", never "are
        // they still good" — so suppressing here would delete the one warning that arrives BEFORE the
        // food dies, and the household would first hear about it the day after.
        var r = ReplenishmentPredictor.Predict(
            DatedAndCounted(3, countedOnDay: 44), D(45), honorExpirations: true, honorQuantity: true);

        Assert.Equal(PredictionStatus.DueSoon, r.Status);
        Assert.False(r.SuppressedByCount);
        Assert.True(r.DueCappedByExpiration);
        Assert.Equal(D(46), r.DueDate);
    }

    [Fact]
    public void ACount_CannotSilenceAnExpiredItem()
    {
        var r = ReplenishmentPredictor.Predict(
            DatedAndCounted(3, countedOnDay: 44), D(50), honorExpirations: true, honorQuantity: true);

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.True(r.Expired);
        Assert.False(r.SuppressedByCount);
    }

    [Fact]
    public void TheSameCount_SuppressesOnceTheLabelIsOutOfTheWay()
    {
        // Control for the two above, and it has to be taken on a day the RHYTHM would ask (due D(50), so
        // D(52) is overdue) — otherwise "not suppressed" would prove nothing, since an item that isn't
        // due isn't being held back by anything. With expiration tracking off the identical product
        // suppresses, so the label really is what stands the count down.
        var r = ReplenishmentPredictor.Predict(DatedAndCounted(3, countedOnDay: 44), D(52), honorQuantity: true);

        Assert.Equal(PredictionStatus.Stocked, r.Status);
        Assert.True(r.SuppressedByCount);
    }

    [Fact]
    public void TheSameCount_WithTheLabelHonoured_StaysOverdueOnThatDay()
    {
        // The pair to the control: same product, same day, expirations ON. Past the label now, so it is
        // pinned expired and the count cannot quiet it.
        var r = ReplenishmentPredictor.Predict(
            DatedAndCounted(3, countedOnDay: 44), D(52), honorExpirations: true, honorQuantity: true);

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.True(r.Expired);
        Assert.False(r.SuppressedByCount);
    }

    [Fact]
    public void AStillLearningItem_IsNotCalledSuppressed()
    {
        // Found by running it: counting a product with only one purchase flipped "Still learning" to
        // "Stocked" and had the page explain that "its rhythm would otherwise have asked for it by now"
        // — about an item that has no rhythm and was never going to be asked for. There has to BE a
        // recommendation before anything can be said to be holding one back. The count itself still
        // shows on the product page; it just isn't dressed up as a decision.
        var product = ProductWithQuantities((0, 1));
        product.TrackQuantity = true;
        product.QuantityOnHand = 3m;
        product.QuantityCountedAt = new DateTimeOffset(D(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var r = ReplenishmentPredictor.Predict(product, D(45), honorQuantity: true);

        Assert.Equal(PredictionStatus.Unknown, r.Status);
        Assert.False(r.SuppressedByCount);
    }

    [Fact]
    public void ARunningLowTappedSinceTheCount_BeatsIt()
    {
        // Newer human evidence beats older human evidence — the same argument the OutNow pin makes.
        // Someone stood at the cupboard today and said it's low; a count from four days ago doesn't
        // get to overrule them.
        var product = CountedAndOverdue(3, countedOnDay: 41);
        product.Signals.Add(Signal(SignalKind.RunningLow, D(45)));

        var r = ReplenishmentPredictor.Predict(product, D(45), honorQuantity: true);

        Assert.Equal(PredictionStatus.Overdue, r.Status); // the rhythm's own verdict, un-suppressed
        Assert.False(r.SuppressedByCount);
    }

    // ---- §13.5: staleness for a count with NO rhythm to project from ------------------------------

    /// <summary>The shape of stock receipts never saw — bought before the app, elsewhere, gifted, or in
    /// one bulk run. 0 or 1 purchases, so no rhythm, so nothing to project exhaustion from.</summary>
    private static Product CountedWithoutARhythm(decimal onHand, int countedOnDay, int purchases = 0)
    {
        var product = purchases == 0 ? ProductWith() : ProductWith([D(0)]);
        product.TrackQuantity = true;
        product.QuantityOnHand = onHand;
        product.QuantityCountedAt = new DateTimeOffset(D(countedOnDay).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return product;
    }

    [Fact]
    public void ACountWithNoRhythm_IsAskedAboutOnAgeAlone()
    {
        // Without this the count would be believed FOREVER — no rhythm means no exhaustion date, and no
        // receipt will ever move stock the app didn't see arrive. So the one population §13 can't
        // automate is also the one it would silently trust the longest.
        var r = ReplenishmentPredictor.Predict(CountedWithoutARhythm(12m, countedOnDay: 0), D(120), honorQuantity: true);

        Assert.True(r.CountLooksStale);
        Assert.Equal(CountConfidence.Aging, r.CountConfidence);
        // No projected date, deliberately: inventing one would be a projection the engine cannot make.
        Assert.Null(r.CountRunsOutOn);
    }

    [Fact]
    public void ACountWithNoRhythm_IsBelievedInsideTheWindow()
    {
        var r = ReplenishmentPredictor.Predict(CountedWithoutARhythm(12m, countedOnDay: 0), D(89), honorQuantity: true);

        Assert.False(r.CountLooksStale);
        Assert.Equal(CountConfidence.Counted, r.CountConfidence);
    }

    [Fact]
    public void TheAgeFallbackAlsoCoversAnItemWithASinglePurchase()
    {
        // One purchase is still no rhythm — the quarter-cow case §13.7 hands off to counting.
        var r = ReplenishmentPredictor.Predict(
            CountedWithoutARhythm(12m, countedOnDay: 0, purchases: 1), D(120), honorQuantity: true);

        Assert.True(r.CountLooksStale);
        Assert.Equal(CountConfidence.Aging, r.CountConfidence);
    }

    [Fact]
    public void AnItemWithARhythm_UsesItsProjectionNotTheAgeFallback()
    {
        // The age fallback must not pre-empt the real check: three on a 10-day rhythm are spent after 30
        // days, and saying so at 45 is the whole point — waiting for day 90 would be worse than useless.
        var r = ReplenishmentPredictor.Predict(CountedAndOverdue(3, countedOnDay: 0), D(45), honorQuantity: true);

        Assert.True(r.CountLooksStale);
        Assert.Equal(CountConfidence.Spent, r.CountConfidence);
        Assert.Equal(D(30), r.CountRunsOutOn);
    }

    [Fact]
    public void AnUncountedProduct_ReportsNotCounted_NotBelieved()
    {
        // `Counted` used to be the enum's zero, so every uncounted product in the catalog reported that its
        // nonexistent number was trustworthy — the sort of implicit answer a surface reads without first
        // checking TrackQuantity. NotCounted is neither believed nor stale.
        var r = ReplenishmentPredictor.Predict(ProductWithQuantities((0, 1), (10, 1)), D(12), honorQuantity: true);

        Assert.Equal(CountConfidence.NotCounted, r.CountConfidence);
        Assert.False(r.CountLooksStale);
    }

    [Fact]
    public void ACountedProductReportsNotCounted_WhenTheCallerDidntAsk()
    {
        // Same inert-by-default doctrine as the flag itself: without honorQuantity the engine never looks at
        // the count, so it must not claim a verdict on it.
        var r = ReplenishmentPredictor.Predict(CountedAndOverdue(3, countedOnDay: 44), D(45));

        Assert.Equal(CountConfidence.NotCounted, r.CountConfidence);
        Assert.False(r.CountLooksStale);
    }

    [Fact]
    public void AFreshCount_ReportsNoStaleReason()
    {
        var r = ReplenishmentPredictor.Predict(CountedAndOverdue(3, countedOnDay: 44), D(45), honorQuantity: true);

        Assert.False(r.CountLooksStale);
        Assert.Equal(CountConfidence.Counted, r.CountConfidence);
    }

    [Fact]
    public void ARunningLowTappedTheSameDayAsTheCount_LosesToIt()
    {
        // The tie that matters, because it's the ordinary flow: you notice it looks low and tap, then you
        // go and actually count three. Dates carry no time of day, so the rule has to be chosen — and the
        // count is the later and far more precise act, the same reason §6.6 gives the purchase the tie.
        var product = CountedAndOverdue(3, countedOnDay: 45);
        product.Signals.Add(Signal(SignalKind.RunningLow, D(45)));

        var r = ReplenishmentPredictor.Predict(product, D(45), honorQuantity: true);

        Assert.Equal(PredictionStatus.Stocked, r.Status);
        Assert.True(r.SuppressedByCount);
    }

    [Fact]
    public void ACountWithNoTypicalTripQuantity_GetsNoHorizonRatherThanAWrongOne()
    {
        // There is deliberately no fallback to the raw median: it would silently restore the per-trip
        // reading this rule exists to remove, on the one product nobody would think to check. No horizon
        // is a visible gap; a wrong horizon is an invisible one.
        // Every write path clamps quantity to >= 1 today, so this is a contract test for the guard rather
        // than a reachable state — which is the point: it pins the guard against those clamps changing.
        var product = ProductWithQuantities((0, 0), (10, 0), (20, 0));
        product.TrackQuantity = true;
        product.QuantityOnHand = 3m;
        product.QuantityCountedAt = new DateTimeOffset(D(0).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var r = ReplenishmentPredictor.Predict(product, D(45), honorQuantity: true);

        Assert.Null(r.CountRunsOutOn);
        Assert.False(r.CountLooksStale);
    }

    [Fact]
    public void ARunningLowFromBeforeTheCount_LosesToIt()
    {
        // The mirror: they said "low" on D(41) and then actually counted three on D(44). The count is
        // the newer look, so it wins — otherwise one old tap would silence counting forever.
        var product = CountedAndOverdue(3, countedOnDay: 44);
        product.Signals.Add(Signal(SignalKind.RunningLow, D(41)));

        var r = ReplenishmentPredictor.Predict(product, D(45), honorQuantity: true);

        Assert.Equal(PredictionStatus.Stocked, r.Status);
        Assert.True(r.SuppressedByCount);
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

    // --- Expiration dates (opt-in): a label fact layered over the rhythms ---
    // The feature is a derived STATE, not a fired event — nothing writes signals, so nothing can
    // double-fire, miss a day the server slept through, or re-flag after a human override.

    private static Product Expiring(params (DateOnly Bought, DateOnly? Expires)[] purchases) =>
        new()
        {
            Id = 1,
            Name = "Milk",
            Purchases = purchases
                .Select(p => new PurchaseEvent { ProductId = 1, PurchasedAt = p.Bought, ExpirationDate = p.Expires })
                .ToList(),
        };

    [Fact]
    public void PassedExpiration_PinsOverdue_WithTheLabelAsDueDate()
    {
        var r = ReplenishmentPredictor.Predict(Expiring((D(0), D(5))), D(6), honorExpirations: true);

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.True(r.Pinned);
        Assert.Equal(D(5), r.DueDate); // "due" = the day it went bad, like OutNow's outage date
        Assert.True(r.Expired);
        Assert.Equal(D(5), r.ExpiresOn);
        Assert.False(r.ExpirationOverridden);
    }

    [Fact]
    public void TheLabeledDayItself_IsStillGood_ButDueToday()
    {
        // "Best by Jul 5" milk is fine ON Jul 5 — expired means today is PAST the label. But the cap
        // makes it DUE today: the label is the last day this stock can serve, so buy now, not tomorrow.
        var r = ReplenishmentPredictor.Predict(Expiring((D(0), D(5))), D(5), honorExpirations: true);

        Assert.False(r.Expired);
        Assert.Equal(D(5), r.ExpiresOn);
        Assert.Equal(D(5), r.DueDate);
        Assert.Equal(PredictionStatus.DueSoon, r.Status);
        Assert.True(r.DueCappedByExpiration);
    }

    [Fact]
    public void FutureLabel_GivesEvenAStillLearningItem_ARealDueDate()
    {
        // One purchase — no rhythm — but you KNOW it dies on D(9): the label alone supplies the due
        // date and lifts the item out of "still learning". Far from the label it's simply Stocked.
        var r = ReplenishmentPredictor.Predict(Expiring((D(0), D(9))), D(2), honorExpirations: true);

        Assert.Equal(D(9), r.ExpiresOn);
        Assert.False(r.Expired);
        Assert.Equal(D(9), r.DueDate);
        Assert.Equal(PredictionStatus.Stocked, r.Status); // window (flat 3) starts D(6)
        Assert.True(r.DueCappedByExpiration);

        var nearer = ReplenishmentPredictor.Predict(Expiring((D(0), D(9))), D(7), honorExpirations: true);
        Assert.Equal(PredictionStatus.DueSoon, nearer.Status); // inside the label's warning window
    }

    [Fact]
    public void TheLabel_HardCaps_ACadenceDueDate()
    {
        // Rhythm says ~every 10 days (due D(30)); the latest jug's label says D(24). The cadence
        // estimates how long stock usually lasts; the label bounds how long it CAN — min, never max.
        var r = ReplenishmentPredictor.Predict(
            Expiring((D(0), null), (D(10), null), (D(20), D(24))), D(21), honorExpirations: true);

        Assert.Equal(D(24), r.DueDate);
        Assert.True(r.DueCappedByExpiration);
        Assert.Equal(PredictionStatus.DueSoon, r.Status); // 3 days out, window ≥ 3
    }

    [Fact]
    public void ALabelAfterTheCadenceDueDate_ChangesNothing()
    {
        // Due D(30) by rhythm, label D(40): the rhythm's earlier date already governs — the label
        // never EXTENDS a projection (that would let a hopeful sticker mute a real warning).
        var r = ReplenishmentPredictor.Predict(
            Expiring((D(0), null), (D(10), null), (D(20), D(40))), D(21), honorExpirations: true);

        Assert.Equal(D(30), r.DueDate);
        Assert.False(r.DueCappedByExpiration);
        Assert.Equal(D(40), r.ExpiresOn); // still surfaced for the panel
    }

    [Fact]
    public void TheCap_NeverCalmsAWarningDown()
    {
        // Statistically OVERDUE (rhythm due D(30), today D(31)) with a hopeful future label D(35):
        // escalate-only — the label can't rescue an item the rhythm says you've already run out of.
        var r = ReplenishmentPredictor.Predict(
            Expiring((D(0), null), (D(10), null), (D(20), D(35))), D(31), honorExpirations: true);

        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.Equal(D(30), r.DueDate); // the rhythm's own date — earlier than the label — stands
        Assert.False(r.DueCappedByExpiration);
    }

    [Fact]
    public void RestockedBeforeTheLabel_DoesNotRemoveTheCap()
    {
        // "I have it" on D(3) doesn't contradict a label that hasn't been proven wrong — the item in
        // hand IS the labeled item. People tap Restocked casually; a casual tap must not silently
        // disarm expiration tracking. (Contrast RestockedAfterTheLabel_Overrides.)
        var product = Expiring((D(0), D(9)));
        product.Signals = [Signal(SignalKind.Restocked, D(3))];

        var r = ReplenishmentPredictor.Predict(product, D(7), honorExpirations: true);

        Assert.Equal(D(9), r.DueDate);
        Assert.True(r.DueCappedByExpiration);
        Assert.False(r.ExpirationOverridden);
    }

    [Fact]
    public void TheOverride_StandsDownTheCapToo()
    {
        // Rhythm due D(30); label D(24) passed; Restocked D(25) overrode it. Half an override would
        // be a lie: the due date returns to the rhythm's own projection, re-anchored by the restock.
        var product = Expiring((D(0), null), (D(10), null), (D(20), D(24)));
        product.Signals = [Signal(SignalKind.Restocked, D(25))];

        var r = ReplenishmentPredictor.Predict(product, D(26), honorExpirations: true);

        Assert.True(r.ExpirationOverridden);
        Assert.False(r.Expired);
        Assert.False(r.DueCappedByExpiration);
        Assert.Equal(D(35), r.DueDate); // restock anchor D(25) + the ~10-day rhythm
    }

    [Fact]
    public void AStockUp_CannotStretchTheProjectionPastTheLabel()
    {
        // Bought 3× the usual on D(20) (projection would stretch to ~D(50)) — but all three jugs die
        // on D(26). The label caps the stock-up too.
        var product = new Product
        {
            Id = 1,
            Name = "Milk",
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0), Quantity = 1 },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(10), Quantity = 1 },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(20), Quantity = 3, ExpirationDate = D(26) },
            ],
        };

        var r = ReplenishmentPredictor.Predict(product, D(21), honorExpirations: true);

        Assert.Equal(D(26), r.DueDate);
        Assert.True(r.DueCappedByExpiration);
    }

    [Fact]
    public void HonorFalse_KeepsTheFeatureFullyDormant()
    {
        // The Settings toggle off: recorded dates neither fire NOR surface — the default parameter is
        // the dormant direction on purpose (a forgotten call site fails inert, not loud).
        var r = ReplenishmentPredictor.Predict(Expiring((D(0), D(5))), D(10));

        Assert.False(r.Expired);
        Assert.Null(r.ExpiresOn);
        Assert.Equal(PredictionStatus.Unknown, r.Status);
    }

    [Fact]
    public void OnlyTheLatestPurchase_CanExpire()
    {
        // Rebuying supersedes the old jug even when the new purchase carries no date at all.
        var r = ReplenishmentPredictor.Predict(
            Expiring((D(0), D(3)), (D(10), null)), D(12), honorExpirations: true);

        Assert.False(r.Expired);
        Assert.Null(r.ExpiresOn);
    }

    [Fact]
    public void SameDayPurchases_TheLongestDateGoverns()
    {
        // Two jugs bought together: you'd open the shorter-dated one first, so the stock as a whole
        // lasts until the LONGER date.
        var product = Expiring((D(0), D(5)), (D(0), D(9)));

        var atSix = ReplenishmentPredictor.Predict(product, D(6), honorExpirations: true);
        Assert.False(atSix.Expired);
        Assert.Equal(D(9), atSix.ExpiresOn);

        var atTen = ReplenishmentPredictor.Predict(product, D(10), honorExpirations: true);
        Assert.True(atTen.Expired);
    }

    [Fact]
    public void RestockedAfterTheLabel_Overrides_AndSaysSo()
    {
        // "I froze it — it's fine": the human's statement, made after the label date, beats the label.
        // The flag is exposed so the expiration panel can SAY it was overridden instead of silently
        // not firing (Jordan's override-indicator requirement).
        var product = Expiring((D(0), D(5)));
        product.Signals = [Signal(SignalKind.Restocked, D(7))];

        var r = ReplenishmentPredictor.Predict(product, D(8), honorExpirations: true);

        Assert.False(r.Expired);
        Assert.True(r.ExpirationOverridden);
        Assert.Equal(D(5), r.ExpiresOn); // the panel still shows which label was overridden
        Assert.False(r.Pinned);
    }

    [Fact]
    public void RestockedOnTheLabeledDay_IsNotAnOverride()
    {
        // On the labeled day nothing showed expired yet — that Restocked was plain "I have it",
        // not a decision to overrule a label the user hadn't been confronted with.
        var product = Expiring((D(0), D(5)));
        product.Signals = [Signal(SignalKind.Restocked, D(5))];

        var r = ReplenishmentPredictor.Predict(product, D(6), honorExpirations: true);

        Assert.True(r.Expired);
        Assert.False(r.ExpirationOverridden);
    }

    [Fact]
    public void ExplicitOutNow_KeepsItsOwnDueDate_WhenAlsoExpired()
    {
        // Both facts hold: the user said "out" on D(7), the label ran out on D(5). The pin's due date
        // stays the user's own statement (the more recent human fact); Expired still reports true so
        // the UI can mention the label too.
        var product = Expiring((D(0), D(5)));
        product.Signals = [Signal(SignalKind.OutNow, D(7))];

        var r = ReplenishmentPredictor.Predict(product, D(8), honorExpirations: true);

        Assert.True(r.Pinned);
        Assert.Equal(D(7), r.DueDate);
        Assert.True(r.Expired);
    }

    [Fact]
    public void Expiration_NeverFeedsEitherRhythm()
    {
        // Identical purchase history with and without labels must learn identical cadences — the
        // label is a fact about the food, not about buying behavior.
        var plain = ReplenishmentPredictor.Predict(ProductWith([D(0), D(10), D(20)]), D(21));
        var dated = ReplenishmentPredictor.Predict(
            Expiring((D(0), D(4)), (D(10), D(14)), (D(20), D(24))), D(21), honorExpirations: true);

        Assert.Equal(plain.RebuyIntervalDays, dated.RebuyIntervalDays);
        Assert.Equal(plain.BurnRateDays, dated.BurnRateDays);
        Assert.Equal(plain.MedianIntervalDays, dated.MedianIntervalDays);
    }

    // --- Basis: the sentence every surface shows (and chat speaks) for "how do you know" ---------

    [Fact]
    public void Basis_SaysLasts_WhenTheBurnRateDrives()
    {
        // The wording IS the meaning: a burn-driven number answers "one lasts ~N days", a rebuy-driven
        // one answers "you buy it ~every N days". Swapping them tells the household the wrong fact in
        // every card, panel, and spoken reply.
        var product = ProductWith(
            [D(0), D(30), D(60)],
            [Signal(SignalKind.OutNow, D(10)), Signal(SignalKind.OutNow, D(40))]);

        Assert.Equal("bought 3×, lasts ~10 days", ReplenishmentPredictor.Predict(product, D(65)).Basis);
    }

    [Fact]
    public void Basis_SaysEvery_WhenTheRebuyRhythmDrives()
    {
        Assert.Equal("bought 2×, ~every 20 days",
            ReplenishmentPredictor.Predict(ProductWith([D(0), D(20)]), D(21)).Basis);
    }

    [Fact]
    public void Basis_ForNoPurchases_AndForStillLearning()
    {
        Assert.Equal("no purchases yet", ReplenishmentPredictor.Predict(ProductWith(), D(1)).Basis);
        Assert.Equal("bought 1×, still learning", ReplenishmentPredictor.Predict(ProductWith([D(0)]), D(1)).Basis);
    }

    // --- §6.6: WHICH active signal wins — newest first, OutNow on a same-instant tie -------------

    [Fact]
    public void TheNewestActiveSignal_Wins_InBothDirections()
    {
        // Low on Tuesday, out on Thursday: the newest statement governs, so the item pins. And the
        // mirror — out on Tuesday, merely low on Thursday — must NOT stay pinned: the household
        // downgraded their own claim, and the engine follows the newer, milder one.
        var outLast = ProductWith([D(0), D(20)],
            [Signal(SignalKind.RunningLow, D(22)), Signal(SignalKind.OutNow, D(24))]);
        var lowLast = ProductWith([D(0), D(20)],
            [Signal(SignalKind.OutNow, D(22)), Signal(SignalKind.RunningLow, D(24))]);

        var pinned = ReplenishmentPredictor.Predict(outLast, D(25));
        Assert.True(pinned.Pinned);
        Assert.Equal(PredictionStatus.Overdue, pinned.Status);

        var softened = ReplenishmentPredictor.Predict(lowLast, D(25));
        Assert.False(softened.Pinned);
        Assert.Equal(PredictionStatus.DueSoon, softened.Status);
        Assert.Equal("Marked running low", softened.SignalNote);
    }

    [Fact]
    public void OutNow_WinsASameInstantTie_AgainstRunningLow()
    {
        // Both taps land on the same instant (dates carry no time): the graver statement governs —
        // "out" and "low" at the same moment means you're out. The tie-break comment in the engine
        // ("OutNow wins a same-instant tie") is this test's subject; it had never been pinned.
        var product = ProductWith([D(0), D(20)],
            [Signal(SignalKind.RunningLow, D(24)), Signal(SignalKind.OutNow, D(24))]);

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.True(r.Pinned);
        Assert.Equal(PredictionStatus.Overdue, r.Status);
        Assert.Equal("Marked out of stock", r.SignalNote);
    }

    // --- BurnCycles: the public one-definition of a completed cycle -------------------------------

    [Fact]
    public void BurnCycles_ASameDayOutage_PairsWithNoPurchase()
    {
        // §6.6's tie at the cycle level: an outage dated the same day as a purchase is ambiguous at
        // date granularity and deliberately closes NO cycle — the strict `>` that keeps §6.6's dead
        // same-day rows out of the burn rhythm (item 41's rule). This is the doctrine's engine-level
        // pin; it existed only as a comment before.
        Assert.Empty(ReplenishmentPredictor.BurnCycles([D(0)], [D(0)]));
    }

    [Fact]
    public void BurnCycles_AnOutageAfterTheNextPurchase_BelongsToThatPurchaseAlone()
    {
        // P1's window closes at P2: an outage after P2 is P2's cycle only. Unbounded windows would
        // hand the same outage to BOTH purchases — a phantom long cycle corrupting the median.
        Assert.Equal([5], ReplenishmentPredictor.BurnCycles([D(0), D(10)], [D(15)]));
    }

    // --- Median internals: the trim's exact boundaries --------------------------------------------

    [Fact]
    public void TheTrim_AppliesAtExactlyThreeIntervals()
    {
        // Gaps {4, 10, 40}: three intervals is the documented "robust enough" floor, so the 40 is
        // dropped (3× median 10 = 30) and the median re-takes to (4+10)/2 = 7. One interval fewer
        // and the trim must NOT apply — Median_IgnoresVacationOutlier pins that side.
        var r = ReplenishmentPredictor.Predict(ProductWith([D(0), D(4), D(14), D(54)]), D(55));

        Assert.Equal(7, r.MedianIntervalDays);
    }

    [Fact]
    public void AnIntervalAtExactlyThreeTimesTheMedian_IsKept()
    {
        // Gaps {2, 10, 30}: 30 is exactly 3× the median — inclusive, so it survives the trim and the
        // median stays 10. Dropping the boundary case would re-take the median over {2, 10} → 6 and
        // silently tighten every such rhythm.
        var r = ReplenishmentPredictor.Predict(ProductWith([D(0), D(2), D(12), D(42)]), D(43));

        Assert.Equal(10, r.MedianIntervalDays);
    }

    [Fact]
    public void TheSpread_UsesTheMedianOfHalves_OnAnEvenSampleCount()
    {
        // Gaps {10, 12, 30, 40} (median 21, 3× = 63 → nothing trims): halves {10,12} and {30,40} →
        // IQR = 35 − 11 = 24. The even-count split takes BOTH middle elements into their halves; a
        // one-off-by-one split reads 29 and miswidths every noisy item's warning window.
        var r = ReplenishmentPredictor.Predict(ProductWith([D(0), D(10), D(22), D(52), D(92)]), D(93));

        Assert.Equal(24, r.IntervalSpreadDays);
    }

    // --- The label's warning window: its own arithmetic, pinned -----------------------------------

    [Fact]
    public void ALabelEqualToTheRhythmsDueDate_IsNotACap()
    {
        // The cap is only a cap when it pulls the date EARLIER. A label landing exactly on the
        // rhythm's own due date changes nothing — reporting it as a cap would have Product Detail
        // explain a constraint that never bound.
        var product = Expiring((D(0), null), (D(10), null), (D(20), D(30)));

        var r = ReplenishmentPredictor.Predict(product, D(21), honorExpirations: true);

        Assert.Equal(D(30), r.DueDate);
        Assert.False(r.DueCappedByExpiration);
        Assert.Equal(D(30), r.ExpiresOn);
    }

    [Fact]
    public void TheLabelsWindow_UsesTheCadences20PercentRule()
    {
        // Rhythm 20 days (gaps {20, 20}, spread null) capped by a D(50) label: the window is
        // max(3, 20% of 20) = 4, so DueSoon starts at D(46) — the label borrows the cadence's own
        // "how twitchy is this item" arithmetic rather than a flat 3.
        var product = Expiring((D(0), null), (D(20), null), (D(40), D(50)));

        Assert.Equal(PredictionStatus.Stocked,
            ReplenishmentPredictor.Predict(product, D(45), honorExpirations: true).Status);
        var inWindow = ReplenishmentPredictor.Predict(product, D(46), honorExpirations: true);
        Assert.Equal(PredictionStatus.DueSoon, inWindow.Status);
        Assert.True(inWindow.DueCappedByExpiration);
        Assert.Equal(D(50), inWindow.DueDate);
    }

    [Fact]
    public void TheLabelsWindow_EarnsWidthFromTheSpread_LikeTheCadenceDoes()
    {
        // Gaps {10, 12, 14, 40}: the 40 trims out (3× median 13 = 39), leaving median 12 with spread
        // IQR({10,12,14}) = 4 — so a D(82) label's window is max(3, max(2.4, 4)) = 4 and DueSoon
        // starts D(78). A window that ignored the spread (or took the smaller of the two rules)
        // would open a day later on exactly the noisy rhythms that earn the wider warning.
        var product = Expiring((D(0), null), (D(10), null), (D(22), null), (D(36), null), (D(76), D(82)));

        Assert.Equal(PredictionStatus.Stocked,
            ReplenishmentPredictor.Predict(product, D(77), honorExpirations: true).Status);
        Assert.Equal(PredictionStatus.DueSoon,
            ReplenishmentPredictor.Predict(product, D(78), honorExpirations: true).Status);
    }

    [Fact]
    public void ALabelTwoDaysAfterTheOnlyPurchase_DoesNotStartLifeDueSoon()
    {
        // The interval-minus-one guard on the label window: bought today, dies the day after
        // tomorrow — the flat 3-day window would open BEFORE the purchase, so the item would be born
        // DueSoon. Clamped to the label−anchor gap minus one, the buy day itself stays Stocked and
        // the warning opens the next day.
        var product = Expiring((D(0), D(2)));

        var bought = ReplenishmentPredictor.Predict(product, D(0), honorExpirations: true);
        Assert.Equal(PredictionStatus.Stocked, bought.Status);
        Assert.True(bought.DueCappedByExpiration);
        Assert.Equal(D(2), bought.DueDate);

        Assert.Equal(PredictionStatus.DueSoon,
            ReplenishmentPredictor.Predict(product, D(1), honorExpirations: true).Status);
    }

    [Fact]
    public void TheDormantToggle_ReportsNoExpirationFlags()
    {
        // Off means DORMANT everywhere on the result: no state flag may leak from a feature the
        // household turned off — a stray true here and a surface explains an override that never ran.
        var r = ReplenishmentPredictor.Predict(Expiring((D(0), D(5))), D(10));

        Assert.False(r.ExpirationOverridden);
        Assert.False(r.DueCappedByExpiration);
    }

    [Fact]
    public void TheToggleOnAPurchaselessProduct_IsANoOp_NotACrash()
    {
        // The census shape with expirations enabled: a counted product that has never been bought has
        // no purchases to read a label from. The purchase-count guard is load-bearing — without it
        // this is Max() over an empty list, and every page that predicts throws for that household.
        var product = CountedWithoutARhythm(12m, countedOnDay: 0);

        var r = ReplenishmentPredictor.Predict(product, D(10), honorExpirations: true, honorQuantity: true);

        Assert.Null(r.ExpiresOn);
        Assert.False(r.Expired);
    }

    // --- The count's exact boundaries ------------------------------------------------------------

    [Fact]
    public void AFreshZeroCount_IsBelieved_AndProjectsNothing()
    {
        // A zero has no exhaustion to project (there is nothing left to run out), so it can never be
        // "Spent" — it goes stale by AGE alone. A projection from zero would land ON the attestation
        // day and instantly disbelieve every fresh "we have none".
        var r = ReplenishmentPredictor.Predict(CountedAndOverdue(0, countedOnDay: 44), D(45), honorQuantity: true);

        Assert.Equal(CountConfidence.Counted, r.CountConfidence);
        Assert.Null(r.CountRunsOutOn);
        Assert.False(r.CountLooksStale);
    }

    [Fact]
    public void OnTheProjectedRunOutDay_TheCountIsStillBelieved()
    {
        // Same convention as the due date and the best-by label: the boundary day itself is not past.
        // Three counted on D(0) at ~10/package run out on D(30) — ON D(30) the count still suppresses;
        // D(31) is the first disbelieved day.
        var onTheDay = ReplenishmentPredictor.Predict(CountedAndOverdue(3, countedOnDay: 0), D(30), honorQuantity: true);
        Assert.Equal(CountConfidence.Counted, onTheDay.CountConfidence);
        Assert.True(onTheDay.SuppressedByCount);
        Assert.Equal(D(30), onTheDay.CountRunsOutOn);

        Assert.Equal(CountConfidence.Spent,
            ReplenishmentPredictor.Predict(CountedAndOverdue(3, countedOnDay: 0), D(31), honorQuantity: true).CountConfidence);
    }

    [Fact]
    public void TheAgeFallback_TripsTheDayAfterNinety_NotOnIt()
    {
        // Day 90 exactly is the last believed day; 91 is the first asked-about one. The same
        // boundary-day convention as everywhere else in the engine.
        Assert.Equal(CountConfidence.Counted,
            ReplenishmentPredictor.Predict(CountedWithoutARhythm(12m, countedOnDay: 0), D(90), honorQuantity: true).CountConfidence);
        Assert.Equal(CountConfidence.Aging,
            ReplenishmentPredictor.Predict(CountedWithoutARhythm(12m, countedOnDay: 0), D(91), honorQuantity: true).CountConfidence);
    }

    // --- Stock-up arithmetic: the ratio, and the medians under it ---------------------------------

    [Fact]
    public void TheStockUpRatio_IsThisBuyOverTheTypicalTrip()
    {
        // Typical trip 2, last buy 6 → factor 3 and a tripled projection. Every earlier stock-up test
        // used a typical trip of ONE, where multiply and divide coincide — this fixture is the one
        // that tells them apart.
        var product = ProductWithQuantities((0, 2), (10, 2), (20, 6));

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.Equal(3.0, r.StockUpFactor);
        Assert.Equal(D(50), r.DueDate); // D(20) + floor(10 × 3)
    }

    [Fact]
    public void TheTypicalTrip_IsTheMedianOfSortedTotals_NotArrivalOrder()
    {
        // Trips of 5, 1, and 9 in that arrival order: the median is 5 (sorted), never 1 (the middle
        // of the unsorted list). Factor = 9/5 = 1.8; an unsorted "median" would read 9/1 = 9 and
        // stretch the projection five times too far.
        var product = ProductWithQuantities((0, 5), (10, 1), (20, 9));

        var r = ReplenishmentPredictor.Predict(product, D(25));

        Assert.Equal(1.8, r.StockUpFactor);
        Assert.Equal(D(38), r.DueDate); // D(20) + floor(10 × 1.8)
    }

    [Fact]
    public void AZeroQuantityRow_FeedsNoTrip()
    {
        // Every write path clamps quantity upward from zero today, so this pins the engine's own
        // defense (the comment beside the filter says exactly that): a zero-quantity row must not
        // mint a zero-total TRIP, because a phantom 0 in the trip medians drags the typical trip
        // down and inflates every stock-up ratio. Here the D(10) row is qty 0: trips are {2, 4}
        // (typical 3, factor 4/3) — with the phantom they'd be {2, 0, 4} (typical 2, factor 2).
        // The zero-qty row still counts as a purchase DATE for the rhythm, deliberately.
        var product = ProductWithQuantities((0, 2), (10, 0), (20, 4));

        var r = ReplenishmentPredictor.Predict(product, D(22));

        Assert.Equal((double)(4m / 3m), r.StockUpFactor);
        Assert.Equal(D(33), r.DueDate); // D(20) + floor(10 × 4/3)
        Assert.Equal(10, r.MedianIntervalDays); // three dates, gaps {10, 10}
    }

    [Fact]
    public void AnEvenTripCount_TakesTheMeanOfTheMiddleTwo()
    {
        // Totals {2, 4} → typical trip 3, so the 4-unit buy is a modest 4/3 stock-up. The even-count
        // median must be the mean of both middles: taking the upper middle (4) reads "nothing
        // unusual" and drops the stretch entirely.
        var product = ProductWithQuantities((0, 2), (10, 4));

        var r = ReplenishmentPredictor.Predict(product, D(12));

        Assert.Equal((double)(4m / 3m), r.StockUpFactor);
        Assert.Equal(D(23), r.DueDate); // D(10) + floor(10 × 4/3)
    }

    // --- Dominant size: the display spelling and the tie-break's recency --------------------------

    [Fact]
    public void TheRecommendedSpelling_IsTheMostRecentNonBlank_InTheWinningBucket()
    {
        // One each-family bucket spelled two ways over time: the hint shows the NEWER spelling
        // ("Each"), not the oldest — the shelf label follows what the store prints now.
        var product = new Product
        {
            Id = 1,
            Name = "Limes",
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0), Size = "1 ct" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(10), Size = "Each" },
            ],
        };

        Assert.Equal("Each", ReplenishmentPredictor.Predict(product, D(12)).RecommendedSize);
    }

    [Fact]
    public void TheTieBreak_ComparesEachBucketsLATESTBuy_NotItsEarliest()
    {
        // Gallons bought first AND most recently (D0, D20); half-gallons in between (D5, D10). Counts
        // tie 2–2, so recency decides — and it must be each bucket's LATEST date: by latest the
        // gallon (D20) wins, by earliest the half-gallon (D5 vs D0) would. The existing tie-break
        // test couldn't tell those apart; this interleaved fixture can.
        var product = new Product
        {
            Id = 1,
            Name = "Whole Milk",
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0), Size = "1 gal" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(5), Size = "1/2 gal" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(10), Size = "1/2 gal" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(20), Size = "1 gal" },
            ],
        };

        Assert.Equal("1 gal", ReplenishmentPredictor.Predict(product, D(25)).RecommendedSize);
    }
}
