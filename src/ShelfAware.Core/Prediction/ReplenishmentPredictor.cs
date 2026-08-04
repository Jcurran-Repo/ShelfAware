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
    /// <summary>How far a stock-up may push a due date: two years. A ceiling on the ARITHMETIC, not on
    /// the behaviour — a genuine freezer hoard is months, so nothing real reaches it, and it never
    /// shortens a rhythm that is legitimately longer than this on its own.</summary>
    private const double MaxProjectionDays = 730;

    /// <summary>How long a count on a product with NO learned rhythm is believed before the app asks again
    /// (§13.5, <see cref="CountConfidence.Aging"/>): 90 days.
    /// <para>It is a judgement, not a derivation, and it has to be — an item with 0 or 1 purchases offers
    /// nothing to project exhaustion from, which is precisely the shape of stock bought before the app,
    /// bought elsewhere, gifted, or in one bulk run. Without this check such a count would be trusted
    /// FOREVER, on the very stock no receipt will ever correct. Ninety days is long enough not to nag a
    /// freezer hoard that genuinely lasts a season, short enough that a count cannot quietly outlive the
    /// food; the cost of being wrong either way is one tap, per §13.5.</para></summary>
    private const int UnattestedCountDays = 90;

    /// <param name="honorExpirations">Whether purchase expiration dates may mark the item out (the
    /// per-household Settings toggle, default OFF — see <c>SettingKeys.TrackExpirationDates</c>).
    /// Defaulting to false makes a call site that forgets the flag fail INERT (no expiry state for a
    /// household that turned it on — a visible gap) rather than fail loud (phantom "expired" pins for
    /// one that turned it off).</param>
    /// <param name="honorQuantity">Whether a counted product's stock may suppress its buy recommendation
    /// (§13.5). Defaults FALSE for the same reason <paramref name="honorExpirations"/> does, and the
    /// direction matters more here: a call site that forgets the flag under-suppresses — it recommends
    /// something you may already have, which is annoying and visible — where defaulting true would let a
    /// forgotten call site SILENCE an item nobody meant to count. Under-nagging is a bug you notice;
    /// over-silence is one you find when you run out. Don't "fix" the default.</param>
    public static PredictionResult Predict(
        Product product, DateOnly today, bool honorExpirations = false, bool honorQuantity = false)
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
            .Select(s => SignalDate.Of(s.SignaledAt))
            .ToList();

        var outageDates = product.Signals
            .Where(s => s.Kind == SignalKind.OutNow)
            .Select(s => SignalDate.Of(s.SignaledAt))
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

        // One trip's worth, by date. Computed once because TWO rules read it and they must not disagree
        // about what a median interval is a median interval OF: the stock-up stretch below scales the
        // interval by (this buy ÷ typical trip), which is the statement "the median interval is how long
        // a TYPICAL TRIP'S worth lasts". §13.5's drift horizon divides by the same typical trip to get
        // days per package. Reading the median as days-per-package in one place and days-per-trip in the
        // other is how a household that buys six at a time got a drift horizon six times too long.
        var tripTotals = product.Purchases
            .Where(p => p.Quantity > 0)
            .GroupBy(p => p.PurchasedAt)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity));
        var typicalTrip = tripTotals.Count > 0 ? MedianDecimal([.. tripTotals.Values]) : 0m;

        // 5. Statistical base: project the driving rhythm from the last time we had stock.
        PredictionStatus status;
        DateOnly? dueDate = null;
        var stockUp = 1.0;

        if (drivingMedian is { } median && lastStockBack is { } anchor)
        {
            // A stock-up stretches the projection: buying ~3× the usual amount pushes the due date out
            // ~3× instead of nagging on the one-unit cadence.
            stockUp = StockUpFactor(tripTotals, typicalTrip, anchor);
            // Bound the STRETCH — an arithmetic sanity check, not the 3× behavioural ceiling that was
            // removed 2026-07-28. Quantity is only clamped upward from zero on the way in, so one
            // misread line (a price or a size read as a count) used to be able to project an item years
            // out, and the failure was SILENT: no exception, just an item that vanishes from every list
            // with nothing saying why. Twelve roasts on a 14-day rhythm is 168 days, nowhere near this,
            // so a real hoard is untouched; only nonsense is. Never shortens a genuinely slow rhythm —
            // the floor is the unstretched median itself. Clamped as a double so the int cast below
            // can't overflow either (500,000,000 threw ArgumentOutOfRangeException before this).
            var projected = Math.Min(median * stockUp, Math.Max(median, MaxProjectionDays));
            // Report the factor that was APPLIED, not the raw ratio. Product Detail renders this as
            // "last buy was ~N× the usual — due date pushed out to match", and once the bound above can
            // bite, those two stop being the same number: a 500× misread that projects 730 days would
            // otherwise claim a 500× stretch the engine never made.
            if (median > 0) stockUp = projected / median;
            var interval = Floor(projected);
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
        //    we're out tonight") — that OutNow is ignored FOREVER, not just today: neither date ever
        //    changes, so the tie never resolves the other way. What works is filing a fresh one
        //    tomorrow, which is why OutNowTodayWouldBeInert exists — a surface inviting "say you're
        //    out" must not promise an act this filter will disregard. Pinned by a unit test.
        var activeSignal = product.Signals
            .Where(s => s.Kind is SignalKind.OutNow or SignalKind.RunningLow)
            .Where(s => lastStockBack is null || SignalDate.Of(s.SignaledAt) > lastStockBack)
            .OrderByDescending(s => s.SignaledAt)
            .ThenByDescending(s => s.Kind == SignalKind.OutNow) // OutNow wins a same-instant tie
            .FirstOrDefault();

        var pinned = false;
        if (activeSignal?.Kind == SignalKind.OutNow)
        {
            status = PredictionStatus.Overdue; // pinned to top until next purchase / restock
            pinned = true;
            dueDate = SignalDate.Of(activeSignal.SignaledAt); // out NOW → due is the outage date
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
                    s.Kind == SignalKind.Restocked && SignalDate.Of(s.SignaledAt) > label);
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

        // 8. The count, last — because it SUPPRESSES a recommendation rather than rewriting a
        //    prediction. Real evidence beats a learned guess, so a counted item with stock on the shelf
        //    drops to Stocked and off the buy lists; everything else on the result (the due date, the
        //    rhythms, the expiry state) is left exactly as computed, so the surfaces can still say WHEN
        //    it would otherwise have been due and why it isn't being asked for.
        //    Never the other way: a count of zero doesn't PIN anything. Reaching zero by arithmetic is a
        //    hypothesis (§13.4) — only a human's "we're out" writes the OutNow that pins, and that runs
        //    through the ordinary signal path above like any other.
        //    Four things it must NOT silence, each for its own reason:
        //      • An OutNow (`pinned`) — a count is a memory of a past look, an OutNow is a statement
        //        about now.
        //      • A LABEL (`dueCapped`, and `expired` which always pins) — a count answers "how many",
        //        never "is it still good". v3.6's cap is escalate-only and must stay that way: silencing
        //        it here would delete exactly the warning it exists to give, and the household would
        //        first hear about the milk the day AFTER it died. Consequence worth knowing: a counted
        //        item with a live label is explained by its label rather than its count, which is the
        //        honest order — the label is the binding constraint.
        //      • A RunningLow tapped since the count — newer human evidence beats older human evidence,
        //        which is the same argument the pin above already makes for OutNow.
        //      • A count that has gone stale (below) — the whole reason the drift check exists.
        var suppressed = false;
        // NotCounted until we actually look at a count — an uncounted product must not report that its
        // absent number is believed.
        var confidence = CountConfidence.NotCounted;
        DateOnly? countRunsOut = null;
        // Entered for ANY established count, zero included — staleness is a question about the NUMBER'S
        // AGE and applies just as much to "none" as to "twelve". Gating this on `> 0` (as it first did)
        // left a stale zero deciding recipe stock outright while a stale positive deferred, which is the
        // same fact treated two ways. Suppression separately still needs `> 0`: a zero has nothing to
        // hold back.
        if (honorQuantity && product is { TrackQuantity: true, QuantityOnHand: not null } counted)
        {
            // There IS a count here, so the question "how much do we believe it" now has an answer;
            // the checks below can only downgrade it.
            confidence = CountConfidence.Counted;

            // Expected exhaustion: the last time a human vouched for the number, plus how long that many
            // packages last. ⚠️ The driving median is how long a TYPICAL TRIP'S worth lasts, not one
            // package — the same reading StockUpFactor uses when it stretches a due date for a big buy.
            // Divide by the typical trip to get days per package, or a household that buys six at a time
            // gets a horizon six times too long and the drift check never fires for exactly the bulk
            // buyers §13 was built for.
            // No typical trip means no divisor, and there is deliberately NO fallback to the raw median:
            // that fallback would silently reinstate the per-trip reading this whole rule exists to
            // remove, on the one product where nobody would think to check. No horizon is a visible gap;
            // a wrong horizon is an invisible one.
            if (counted.QuantityCountedAt is { } countedAt)
            {
                var attestedOn = SignalDate.Of(countedAt);
                // A projection needs something to exhaust, so a count of zero can only ever go stale by
                // age — no rhythm can say when "none" stops being true.
                if (counted.QuantityOnHand.Value > 0 && drivingMedian is { } perTrip && perTrip > 0 && typicalTrip > 0)
                {
                    var perPackage = perTrip / (double)typicalTrip;
                    countRunsOut = attestedOn
                        .AddDays(Floor(Math.Min(perPackage * (double)counted.QuantityOnHand.Value, MaxProjectionDays)));
                    if (today > countRunsOut.Value) confidence = CountConfidence.Spent;
                }
                // No rhythm to project from — 0 or 1 purchases, the shape of stock receipts never saw.
                // §13.5's drift check would simply not apply, so the count would be believed forever on
                // exactly the items nothing will ever correct. Fall back to asking on AGE alone. There is
                // deliberately no `countRunsOut` here: inventing a date would be a projection the engine
                // cannot make, and `CountConfidence` is what tells a surface to word it differently.
                else if (today.DayNumber - attestedOn.DayNumber > UnattestedCountDays)
                {
                    confidence = CountConfidence.Aging;
                }
            }

            // `expired` needs no term of its own here: step 7 pins whenever it expires, so `!pinned`
            // below already covers it. Naming it twice would suggest the two can come apart.
            var labelIsSpeaking = dueCapped;
            // STRICTLY after the count. A low tapped on the same DAY loses, because the realistic order
            // is "this looks low" → then go and actually count: the count is the later and far more
            // precise act, and dates here have no time-of-day to separate them. This is the same shape
            // as §6.6's documented same-day tie, resolved for the same reason — the more specific
            // statement wins.
            var lowSinceTheCount = activeSignal is { Kind: SignalKind.RunningLow } low
                && (counted.QuantityCountedAt is not { } at || SignalDate.Of(low.SignaledAt) > SignalDate.Of(at));
            // …and there has to BE a recommendation to hold back. A still-learning item was never going
            // to be asked for, so calling it suppressed would make every surface explain a decision the
            // engine never made — "its rhythm would otherwise have asked for it by now", about an item
            // with no rhythm. Stocked is already off the buy lists, so this only ever skips work there.
            var wouldHaveAsked = status is PredictionStatus.Overdue or PredictionStatus.DueSoon;

            if (counted.QuantityOnHand.Value > 0
                && wouldHaveAsked && !pinned && !labelIsSpeaking && !lowSinceTheCount
                && confidence == CountConfidence.Counted)
            {
                suppressed = true;
                status = PredictionStatus.Stocked;
            }
        }

        return new PredictionResult
        {
            ProductId = product.Id,
            Status = status,
            DueDate = dueDate,
            SuppressedByCount = suppressed,
            CountConfidence = confidence,
            CountRunsOutOn = countRunsOut,
            MedianIntervalDays = drivingMedian, // the winning number — shown everywhere
            RebuyIntervalDays = rebuy?.Median,
            BurnRateDays = burn?.Median,
            IntervalSpreadDays = spread,
            StockUpFactor = stockUp > 1.0 ? stockUp : null,
            Basis = BuildBasis(allPurchaseDates.Count, drivingMedian, burnDrives),
            SignalNote = SignalNoteFor(activeSignal?.Kind),
            RecommendedSize = dominantSize,
            Pinned = pinned,
            // Beside the tie rule in spirit: == today is exactly "a signal dated now fails the strict->
            // filter above". Null lastStockBack compares false, which is right — with no stock-back a
            // fresh OutNow is always active.
            OutNowTodayWouldBeInert = lastStockBack == today,
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

    /// <summary>Every COMPLETED burn cycle: for each purchase, the days to the first outage after it and
    /// before the next purchase — one cycle per purchase. Restocks are ignored here too; only a real
    /// purchase starts a cycle.
    /// <para>Public because "has this item EVER actually run out?" is a question from outside the
    /// prediction path — v4.0's backlog check (DESIGN.md §13.7) reads the COUNT, where
    /// <see cref="PredictionResult.BurnRateDays"/> can't answer it (that stays null at one cycle as
    /// well as none). There must be exactly one definition of a completed cycle, so this is it.</para>
    /// Expects <paramref name="purchaseDates"/> distinct + ascending and <paramref name="outageDates"/>
    /// ascending, as <see cref="Predict"/> and <see cref="Reporting.BacklogSignals"/> both prepare them.</summary>
    public static IReadOnlyList<int> BurnCycles(IReadOnlyList<DateOnly> purchaseDates, IReadOnlyList<DateOnly> outageDates)
    {
        if (purchaseDates.Count == 0 || outageDates.Count == 0) return [];

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

        return cycles;
    }

    // Burn rate: the median of the completed cycles. Needs ≥2 of them; null otherwise.
    private static Rhythm? BurnRate(IReadOnlyList<DateOnly> purchaseDates, IReadOnlyList<DateOnly> outageDates)
    {
        var cycles = BurnCycles(purchaseDates, outageDates);
        return cycles.Count >= 2 ? MedianWithTrim([.. cycles]) : null;
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
    // quantities).
    //
    // NOT capped (the 3× ceiling was removed 2026-07-28). Buy twelve when you usually buy one and you
    // have twelve; a ceiling meant the app started asking for more while nine were still in the freezer,
    // which is the exact behaviour v4.0 exists to stop. Nothing in the data ever justified "3" — it was
    // a guard against one bulk run silencing an item for a year, and that guard is better served
    // elsewhere: a DATED item can't stretch past its label either way (the expiration cap is
    // escalate-only and applies on top of this), so the uncapped range only ever covers undated
    // non-perishables — precisely the things it IS safe to stay quiet about. The honest limit remains:
    // this can't tell a freezer stock-up from twenty sodas bought for a party, and neither could the
    // cap; a real count (§13) is what actually answers it.
    private static double StockUpFactor(
        IReadOnlyDictionary<DateOnly, decimal> tripTotals, decimal typicalTrip, DateOnly anchor)
    {
        if (!tripTotals.TryGetValue(anchor, out var lastQty)) return 1.0; // anchor is a restock, not a buy
        if (typicalTrip <= 0 || lastQty <= typicalTrip) return 1.0;
        return (double)(lastQty / typicalTrip);
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
