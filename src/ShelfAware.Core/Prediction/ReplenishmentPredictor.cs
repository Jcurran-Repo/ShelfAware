using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Prediction;

/// <summary>
/// Pure, deterministic replenishment predictor (DESIGN.md §6). No LLM, no I/O — give it a
/// product's purchase history and signals plus "today" and it returns a <see cref="PredictionResult"/>.
/// Side-effect-free and fully unit-testable; the prediction path never calls the model (§0.4).
/// </summary>
public static class ReplenishmentPredictor
{
    public static PredictionResult Predict(Product product, DateOnly today)
    {
        // 1. Distinct purchase dates — same-day events collapse (§6.1). Restocked signals count
        //    as purchase-equivalent dates for the interval math (§6.6).
        var purchaseDates = product.Purchases
            .Select(p => p.PurchasedAt)
            .Distinct()
            .ToList();

        var restockDates = product.Signals
            .Where(s => s.Kind == SignalKind.Restocked)
            .Select(s => DateOnly.FromDateTime(s.SignaledAt.Date))
            .ToList();

        var eventDates = purchaseDates
            .Concat(restockDates)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        // Which OutNow/RunningLow signal (if any) is still in effect? A later purchase or restock
        // clears it — §6.6: OutNow holds "until next purchase or Restocked".
        var lastEvent = eventDates.Count > 0 ? eventDates[^1] : (DateOnly?)null;
        var activeSignal = product.Signals
            .Where(s => s.Kind is SignalKind.OutNow or SignalKind.RunningLow)
            .Where(s => lastEvent is null || DateOnly.FromDateTime(s.SignaledAt.Date) > lastEvent)
            .OrderByDescending(s => s.SignaledAt)
            .ThenByDescending(s => s.Kind == SignalKind.OutNow) // OutNow wins a same-instant tie
            .FirstOrDefault();

        // 2–5. Statistical base prediction.
        PredictionStatus status;
        DateOnly? dueDate = null;
        double? medianDays = null;

        if (eventDates.Count >= 2)
        {
            var intervals = new List<int>();
            for (var i = 1; i < eventDates.Count; i++)
            {
                intervals.Add(eventDates[i].DayNumber - eventDates[i - 1].DayNumber);
            }

            var median = Median(intervals);

            // ≥4 events → discard intervals longer than 3× median, then re-take the median (§6.3).
            if (eventDates.Count >= 4)
            {
                var trimmed = intervals.Where(d => d <= 3 * median).ToList();
                if (trimmed.Count > 0)
                {
                    median = Median(trimmed);
                }
            }

            medianDays = median;
            var lastPurchase = eventDates[^1];
            dueDate = lastPurchase.AddDays(Round(median));

            var threshold = Round(Math.Max(3.0, 0.2 * median));
            var dueSoonStart = dueDate.Value.AddDays(-threshold);

            if (today > dueDate.Value) status = PredictionStatus.Overdue;
            else if (today >= dueSoonStart) status = PredictionStatus.DueSoon;
            else status = PredictionStatus.Stocked;
        }
        else
        {
            status = PredictionStatus.Unknown;
        }

        // 6. Signal overrides on top of the statistical base.
        var pinned = false;
        if (activeSignal?.Kind == SignalKind.OutNow)
        {
            status = PredictionStatus.Overdue; // pinned to top until next purchase / Restocked
            pinned = true;
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
            MedianIntervalDays = medianDays,
            Basis = BuildBasis(purchaseDates.Count, medianDays),
            SignalNote = SignalNoteFor(activeSignal?.Kind),
            Pinned = pinned
        };
    }

    private static double Median(IReadOnlyList<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    // Days are whole; round halves away from zero so a 12.5-day median reads as 13, not banker's 12.
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static string BuildBasis(int purchaseCount, double? medianDays) =>
        medianDays is { } m
            ? $"bought {purchaseCount}×, ~every {Round(m)} days"
            : purchaseCount == 0 ? "no purchases yet" : $"bought {purchaseCount}×, still learning";

    private static string? SignalNoteFor(SignalKind? activeSignal) => activeSignal switch
    {
        SignalKind.OutNow => "Marked out of stock",
        SignalKind.RunningLow => "Marked running low",
        _ => null,
    };
}
