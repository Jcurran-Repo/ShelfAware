namespace ShelfAware.Core.Prediction;

/// <summary>
/// Outcome of <see cref="ReplenishmentPredictor.Predict"/> for one product (DESIGN.md §6.7).
/// </summary>
public record PredictionResult
{
    public required int ProductId { get; init; }
    public required PredictionStatus Status { get; init; }

    /// Predicted run-out date; null when status is <see cref="PredictionStatus.Unknown"/>.
    public DateOnly? DueDate { get; init; }

    /// The driving ("winning") interval in days — the burn rate when it's available, else the rebuy
    /// rhythm. Shown everywhere. Null when there is too little history.
    public double? MedianIntervalDays { get; init; }

    /// Rebuy rhythm: the median gap between purchases ("you buy this ~every N days"). Null with &lt; 2
    /// purchases. Shown alongside <see cref="BurnRateDays"/> on Product Detail.
    public double? RebuyIntervalDays { get; init; }

    /// Burn rate: how long a purchase lasts before running out (median purchase→outage), once there are
    /// ≥ 2 completed outage cycles. Null otherwise. The gap from <see cref="RebuyIntervalDays"/> reveals
    /// how long you typically go without before restocking.
    public double? BurnRateDays { get; init; }

    /// The cadence's "give or take": interquartile range of the driving rhythm's (trimmed) samples, in
    /// days. Null with fewer than 3 samples. A noisy rhythm widens the DueSoon warning window; a
    /// metronomic one keeps it tight — and the UI can say "~every 12 days, give or take 3".
    public double? IntervalSpreadDays { get; init; }

    /// &gt; 1 when the most recent stock-back was a purchase noticeably bigger than the typical trip
    /// (same-day quantities summed), in which case the due date is projected that much further out —
    /// buying 3 bags shouldn't nag on the 1-bag cadence. Null when the last buy was typical (or the
    /// anchor was a restock). Capped at 3×.
    public double? StockUpFactor { get; init; }

    /// The package size to recommend buying — the size bought most often (ties: most recent). Null for
    /// items with no recorded size. Different sizes roll up into one product; this picks the one to suggest.
    public string? RecommendedSize { get; init; }

    /// Short statistical explanation for UI transparency, e.g. "bought 5×, ~every 12 days".
    public required string Basis { get; init; }

    /// Active user-statement note, e.g. "Marked out of stock"; null when none. Kept separate from
    /// Basis so the UI can present the prediction and the user's own signal as distinct cues.
    public string? SignalNote { get; init; }

    /// True when an active OutNow signal pins this item to the top of the list (§6.6 / §8).
    public bool Pinned { get; init; }
}
