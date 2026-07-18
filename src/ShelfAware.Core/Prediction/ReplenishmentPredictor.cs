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
    /// <param name="honorExpirations">Whether purchase expiration dates may mark the item out (the
    /// per-household Settings toggle, default OFF — see <c>SettingKeys.TrackExpirationDates</c>).
    /// Defaulting to false makes a call site that forgets the flag fail INERT (no expiry state for a
    /// household that turned it on — a visible gap) rather than fail loud (phantom "expired" pins for
    /// one that turned it off).</param>
    public static PredictionResult Predict(Product product, DateOnly today, bool honorExpirations = false)
    {
        // 1. Distinct purchase dates — same-day events collapse (§6.1). Size is metadata, not identity: an
        //    item bought in random sizes (milk as a half-gallon or a gallon) is ONE product. Learn the
        //    rebuy rhythm from the DOMINANT size's purchases when that size has enough history (≥2 buys),
        //    else fall back to ALL purchases so a mixed-size item still predicts. Either way recommend the
        //    dominant size. Sizes group via SizeBucket, so the loose/"each" spellings extraction writes
        //    inconsistently (null vs "Each" vs "1 ct") are ONE size — otherwise an item bought weekly
        //    with alternating spellings would learn a cadence from every-other purchase and warn late.
        var dominantSize = DominantSize(product.Purchases);
        var dominantDates = product.Purchases
            .Where(p => SizeBucket.Key(p.Size) == SizeBucket.Key(dominantSize))
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

        // 7. Expiration on top of everything: a dated label is a FACT the two rhythms can't see — milk
        //    goes bad whether or not you drank it. Only the LATEST purchase's date governs (rebuying
        //    supersedes the old jug, whether or not the new one carries a date); among same-day
        //    purchases the LONGEST date wins (you'd open the shorter-dated one first). "Best by"
        //    convention: the labeled day itself is still good — expired means today is PAST it.
        //
        //    Before the label: the date is a HARD CAP on the due date — the cadence estimates how long
        //    stock usually lasts, the label bounds how long it CAN last, so the projection never
        //    extends past it (min, never max). The cap ESCALATES only: it can pull a due date earlier
        //    and bump status up (it even gives a still-learning item a real due date — one purchase of
        //    milk has no rhythm, but you know it dies on the 25th), never calms a warning down. Through
        //    the cap an expiring item flows into Due Soon → the lists BEFORE it dies, not just after.
        //
        //    A Restocked dated after the label is the human overriding it ("I froze it — still good")
        //    and stands down the label entirely — pin AND cap — until the next purchase resets things.
        //    A Restocked ON or BEFORE the labeled day is just "I have it": the item in hand IS the
        //    labeled item and the label hasn't been contradicted yet, so the cap stands. That
        //    asymmetry is deliberate — people tap Restocked casually, and a casual tap must not
        //    silently disarm expiration tracking. Nothing here feeds either rhythm.
        DateOnly? expiresOn = null;
        bool expired = false, expirationOverridden = false, dueCapped = false;
        if (honorExpirations && product.Purchases.Count > 0)
        {
            var latestBuy = product.Purchases.Max(p => p.PurchasedAt);
            expiresOn = product.Purchases
                .Where(p => p.PurchasedAt == latestBuy)
                .Max(p => p.ExpirationDate);
            if (expiresOn is { } label)
            {
                expirationOverridden = product.Signals.Any(s =>
                    s.Kind == SignalKind.Restocked && DateOnly.FromDateTime(s.SignaledAt.Date) > label);
                if (!expirationOverridden && today > label)
                {
                    expired = true;
                    if (!pinned) // an explicit OutNow already pins; the user's own outage date stands
                    {
                        status = PredictionStatus.Overdue;
                        pinned = true;
                        dueDate = label; // "due" = the day it went bad, like OutNow's outage date
                    }
                }
                else if (!expirationOverridden && (dueDate is null || label < dueDate.Value))
                {
                    dueDate = label;
                    dueCapped = true;
                    // The label's warning window: the cadence's own threshold when a rhythm exists
                    // (a noisy rhythm warns earlier here too), the flat 3-day rule for a learner —
                    // and never wider than the label leaves room for (the interval-minus-one guard,
                    // so a next-day label doesn't start life already DueSoon).
                    var labelThreshold = drivingMedian is { } lm
                        ? Round(Math.Max(3.0, Math.Max(0.2 * lm, spread ?? 0)))
                        : 3;
                    if (lastStockBack is { } anchor2)
                        labelThreshold = Math.Min(labelThreshold, Math.Max(0, label.DayNumber - anchor2.DayNumber - 1));
                    var labelStatus = today >= label.AddDays(-labelThreshold)
                        ? PredictionStatus.DueSoon
                        : PredictionStatus.Stocked;
                    if (labelStatus > status) status = labelStatus; // escalate-only (lifts Unknown too)
                }
            }
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
            ExpiresOn = expiresOn,
            Expired = expired,
            ExpirationOverridden = expirationOverridden,
            DueCappedByExpiration = dueCapped,
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

    // The size bucket bought most often; ties broken by the most recently purchased. Drives both the
    // cadence (predict from this bucket's purchases) and what we recommend. The display string is the
    // most recent NON-blank spelling in the winning bucket — only the each-family mixes null with
    // literal spellings, and a null there would silently drop the size hint the older buys carried.
    // Null when the bucket has no spelled size at all (an all-null each-family product).
    private static string? DominantSize(IReadOnlyCollection<PurchaseEvent> purchases)
    {
        if (purchases.Count == 0) return null;
        return purchases
            .GroupBy(p => SizeBucket.Key(p.Size))
            .Select(g => new
            {
                Display = g.OrderByDescending(p => p.PurchasedAt)
                    .Select(p => p.Size)
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                Count = g.Count(),
                Latest = g.Max(p => p.PurchasedAt),
            })
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.Latest)
            .Select(x => x.Display)
            .First();
    }

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
