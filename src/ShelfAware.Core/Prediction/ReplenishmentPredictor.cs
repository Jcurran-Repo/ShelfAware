using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Prediction;

/// <summary>
/// Pure, deterministic replenishment predictor (DESIGN.md §6). No LLM, no I/O — give it a
/// product's purchase history and signals plus "today" and it returns a <see cref="PredictionResult"/>.
/// Side-effect-free and fully unit-testable; the prediction path never calls the model (§0.4).
///
/// Two purchase-anchored rhythms, learned only from real purchases (never restocks):
///   • Rebuy rhythm — median gap between consecutive purchases ("you buy this ~every 12 days").
///   • Burn rate    — for each purchase, days to the first "out now" after it ("one lasts ~9 days").
/// Hybrid: burn rate drives the run-out prediction once there are ≥2 completed outage cycles (it's the
/// truer answer to "when will I run out"); otherwise fall back to the rebuy rhythm.
/// </summary>
public static class ReplenishmentPredictor
{
    public static PredictionResult Predict(Product product, DateOnly today)
    {
        // 1. Distinct purchase dates — same-day events collapse (§6.1). Size is metadata, not identity: an
        //    item bought in random sizes (milk as a half-gallon or a gallon) is ONE product. Learn the
        //    rebuy rhythm from the DOMINANT size's purchases when that size has enough history (≥2 buys),
        //    else fall back to ALL purchases so a mixed-size item still predicts. Either way recommend the
        //    dominant size.
        var dominantSize = DominantSize(product.Purchases);
        var dominantDates = product.Purchases
            .Where(p => SizeKey(p.Size) == SizeKey(dominantSize))
            .Select(p => p.PurchasedAt)
            .Distinct()
            .ToList();
        var allPurchaseDates = product.Purchases.Select(p => p.PurchasedAt).Distinct().OrderBy(d => d).ToList();
        var rebuyDates = dominantDates.Count >= 2 ? dominantDates : allPurchaseDates;

        var restockDates = product.Signals
            .Where(s => s.Kind == SignalKind.Restocked)
            .Select(s => DateOnly.FromDateTime(s.SignaledAt.Date))
            .ToList();

        var outageDates = product.Signals
            .Where(s => s.Kind == SignalKind.OutNow)
            .Select(s => DateOnly.FromDateTime(s.SignaledAt.Date))
            .OrderBy(d => d)
            .ToList();

        // "Last time I had stock" — a purchase OR a restock ("found one"). A restock anchors the projected
        // due date (so restocking clears an out/overdue state) but does NOT feed either learned rhythm:
        // only real buys count (Jordan: "count it if I bought one, not if I found one"). This also clears an
        // OutNow — any signal older than the last stock-back is no longer in effect (§6.6).
        DateOnly? lastStockBack = allPurchaseDates.Concat(restockDates).Cast<DateOnly?>().Max();

        // 2–3. The two rhythms, both from purchases only.
        var rebuy = MedianInterval(rebuyDates);
        var burn = BurnRate(allPurchaseDates, outageDates);

        // 4. Hybrid: burn rate drives once we have ≥2 outage cycles; else the rebuy rhythm.
        var burnDrives = burn is not null;
        var driving = burn ?? rebuy;
        double? drivingMedian = driving?.Median;
        // The cadence's "give or take": IQR of the driving rhythm's samples. With < 3 samples a spread
        // is meaningless, so it stays null and the warning window falls back to the flat rule.
        double? spread = driving is { Samples.Count: >= 3 } d ? Iqr(d.Samples) : null;

        // 5. Statistical base: project the driving rhythm from the last time we had stock.
        PredictionStatus status;
        DateOnly? dueDate = null;
        var stockUp = 1.0;

        if (drivingMedian is { } median && lastStockBack is { } anchor)
        {
            // A stock-up stretches the projection: buying ~3× the usual amount pushes the due date out
            // ~3× instead of nagging on the one-unit cadence.
            stockUp = StockUpFactor(product.Purchases, anchor);
            var interval = Floor(median * stockUp);
            dueDate = anchor.AddDays(interval);
            // The DueSoon window earns its width from the cadence's real variance: a noisy rhythm
            // (IQR above the flat max(3, 20%) rule) warns earlier; a metronomic one stays tight. But it
            // must never span the WHOLE cycle: for a short-cadence item (milk bought ~every 3 days) the
            // flat 3-day floor would drop it straight back into "due soon" the instant you restock it, so
            // it could never leave Running Low. Cap the window at one day inside the interval — a fresh
            // restock always earns at least one "stocked" day before the countdown starts again.
            var threshold = Round(Math.Max(3.0, Math.Max(0.2 * median, spread ?? 0)));
            threshold = Math.Min(threshold, Math.Max(0, interval - 1));
            var dueSoonStart = dueDate.Value.AddDays(-threshold);

            if (today > dueDate.Value) status = PredictionStatus.Overdue;
            else if (today >= dueSoonStart) status = PredictionStatus.DueSoon;
            else status = PredictionStatus.Stocked;
        }
        else
        {
            status = PredictionStatus.Unknown;
        }

        // 6. Signal overrides on top of the statistical base. Active = STRICTLY later than the last
        //    stock-back. Same-day ties are ambiguous at date granularity (purchases carry no time), and
        //    the purchase deliberately wins: the primary flow is "item pinned Overdue → [Bought today]",
        //    which must clear the pin. The cost is the rare inverse ("bought this morning, discovered
        //    we're out tonight") — that OutNow is ignored until tomorrow. Pinned by a unit test.
        var activeSignal = product.Signals
            .Where(s => s.Kind is SignalKind.OutNow or SignalKind.RunningLow)
            .Where(s => lastStockBack is null || DateOnly.FromDateTime(s.SignaledAt.Date) > lastStockBack)
            .OrderByDescending(s => s.SignaledAt)
            .ThenByDescending(s => s.Kind == SignalKind.OutNow) // OutNow wins a same-instant tie
            .FirstOrDefault();

        var pinned = false;
        if (activeSignal?.Kind == SignalKind.OutNow)
        {
            status = PredictionStatus.Overdue; // pinned to top until next purchase / restock
            pinned = true;
            dueDate = DateOnly.FromDateTime(activeSignal.SignaledAt.Date); // out NOW → due is the outage date
        }
        else if (activeSignal?.Kind == SignalKind.RunningLow && status is PredictionStatus.Stocked or PredictionStatus.Unknown)
        {
            status = PredictionStatus.DueSoon; // "at least DueSoon"
        }

        return new PredictionResult
        {
            ProductId = product.Id,
            Status = status,
            DueDate = dueDate,
            MedianIntervalDays = drivingMedian, // the winning number — shown everywhere
            RebuyIntervalDays = rebuy?.Median,
            BurnRateDays = burn?.Median,
            IntervalSpreadDays = spread,
            StockUpFactor = stockUp > 1.0 ? stockUp : null,
            Basis = BuildBasis(allPurchaseDates.Count, drivingMedian, burnDrives),
            SignalNote = SignalNoteFor(activeSignal?.Kind),
            RecommendedSize = dominantSize,
            Pinned = pinned,
        };
    }

    // A learned rhythm: the (trimmed) median plus the samples it came from, so callers can also
    // measure the spread of the same numbers the median was taken over.
    private sealed record Rhythm(double Median, List<int> Samples);

    // Rebuy rhythm: median gap between consecutive purchase dates. ≥4 dates (≥3 gaps) → discard gaps longer
    // than 3× median and re-take it (§6.3). Null with fewer than 2 dates.
    private static Rhythm? MedianInterval(IReadOnlyList<DateOnly> dates)
    {
        if (dates.Count < 2) return null;
        var sorted = dates.OrderBy(d => d).ToList();
        var intervals = new List<int>();
        for (var i = 1; i < sorted.Count; i++)
        {
            intervals.Add(sorted[i].DayNumber - sorted[i - 1].DayNumber);
        }
        return MedianWithTrim(intervals);
    }

    // Burn rate: for each purchase, the days to the FIRST outage after it (before the next purchase) — one
    // cycle per purchase. Median of those cycles. Needs ≥2 completed cycles; null otherwise. Restocks are
    // ignored here too — only a real purchase starts a burn cycle.
    private static Rhythm? BurnRate(IReadOnlyList<DateOnly> purchaseDates, IReadOnlyList<DateOnly> outageDates)
    {
        if (purchaseDates.Count == 0 || outageDates.Count == 0) return null;

        var cycles = new List<int>();
        for (var i = 0; i < purchaseDates.Count; i++)
        {
            var start = purchaseDates[i];
            DateOnly? nextPurchase = i + 1 < purchaseDates.Count ? purchaseDates[i + 1] : null;
            var outage = outageDates
                .Where(o => o > start && (nextPurchase is not { } np || o < np))
                .Cast<DateOnly?>()
                .FirstOrDefault();
            if (outage is { } o) cycles.Add(o.DayNumber - start.DayNumber);
        }

        return cycles.Count >= 2 ? MedianWithTrim(cycles) : null;
    }

    private static Rhythm MedianWithTrim(List<int> intervals)
    {
        var median = Median(intervals);
        var samples = intervals;
        if (intervals.Count >= 3) // ≥3 data points → robust enough to drop a stock-up/vacation outlier
        {
            var trimmed = intervals.Where(d => d <= 3 * median).ToList();
            if (trimmed.Count > 0)
            {
                median = Median(trimmed);
                samples = trimmed;
            }
        }
        return new Rhythm(median, samples);
    }

    // Interquartile range (median-of-halves method) — the robust "give or take" on a rhythm.
    private static double Iqr(List<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        var lower = sorted.Take(mid).ToList();
        var upper = sorted.Skip(sorted.Count % 2 == 1 ? mid + 1 : mid).ToList();
        return Median(upper) - Median(lower);
    }

    // A stock-up stretches the projection: when the anchor (last stock-back) is a purchase date whose
    // same-day summed quantity is above the typical per-trip quantity, scale the interval by the ratio.
    // Extend-only — buying LESS than usual doesn't shorten the projection (too twitchy on noisy
    // quantities) — and capped at 3× so one bulk run can't push an item out of sight for a year.
    private static double StockUpFactor(IReadOnlyCollection<PurchaseEvent> purchases, DateOnly anchor)
    {
        var totalsByDate = purchases
            .Where(p => p.Quantity > 0)
            .GroupBy(p => p.PurchasedAt)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity));
        if (!totalsByDate.TryGetValue(anchor, out var lastQty)) return 1.0; // anchor is a restock, not a buy
        var typical = MedianDecimal([.. totalsByDate.Values]);
        if (typical <= 0 || lastQty <= typical) return 1.0;
        return Math.Min((double)(lastQty / typical), 3.0);
    }

    private static decimal MedianDecimal(List<decimal> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2m;
    }

    // The size bought most often; ties broken by the most recently purchased size. Null when no purchase
    // carries a size. Drives both the cadence (predict from this size's purchases) and what we recommend.
    private static string? DominantSize(IReadOnlyCollection<PurchaseEvent> purchases)
    {
        if (purchases.Count == 0) return null;
        return purchases
            .GroupBy(p => SizeKey(p.Size))
            .Select(g => new
            {
                Display = g.OrderByDescending(p => p.PurchasedAt).First().Size,
                Count = g.Count(),
                Latest = g.Max(p => p.PurchasedAt),
            })
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.Latest)
            .Select(x => x.Display)
            .First();
    }

    private static string SizeKey(string? size) => (size ?? "").Trim().ToLowerInvariant();

    private static double Median(IReadOnlyList<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    // The DueSoon-window threshold rounds halves away from zero (e.g. 12.5 → 13).
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    // The predicted run-out interval rounds DOWN (floor): the due date lands a touch early, so we err
    // toward reminding *before* an item runs out rather than after. Pairs with buy-quantity rounding UP.
    private static int Floor(double value) => (int)Math.Floor(value);

    private static string BuildBasis(int purchaseCount, double? drivingMedian, bool burnDrives) =>
        drivingMedian is { } m
            ? burnDrives
                ? $"bought {purchaseCount}×, lasts ~{Floor(m)} days"
                : $"bought {purchaseCount}×, ~every {Floor(m)} days"
            : purchaseCount == 0 ? "no purchases yet" : $"bought {purchaseCount}×, still learning";

    private static string? SignalNoteFor(SignalKind? activeSignal) => activeSignal switch
    {
        SignalKind.OutNow => "Marked out of stock",
        SignalKind.RunningLow => "Marked running low",
        _ => null,
    };
}
