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

    /// The governing expiration date — the LATEST purchase's labeled date (same-day purchases: the
    /// longest, since you'd open the shorter-dated one first). Null when the latest purchase carries no
    /// date or expiration tracking is off. A future value lets the UI say "expires Jul 22".
    public DateOnly? ExpiresOn { get; init; }

    /// True when <see cref="ExpiresOn"/> has passed and no later Restocked overrode it: the item is
    /// pinned Overdue with DueDate = the labeled date. Kept distinct from a user's OutNow so the UI can
    /// say "expired Jul 18" rather than "marked out" — the user can see the jug in the fridge, and a
    /// state that explains itself is the difference between trusted and gaslighting.
    public bool Expired { get; init; }

    /// True when the label has passed but a Restocked signal dated AFTER the labeled date suppressed it —
    /// the human overriding the label ("I froze it; it's fine"). Surfaced so the expiration panel can say
    /// the label was overridden instead of silently not firing. The override stands down the DueDate cap
    /// too — half an override would be a lie.
    public bool ExpirationOverridden { get; init; }

    /// True when <see cref="DueDate"/> IS the label rather than the rhythm: the cadence projected past
    /// <see cref="ExpiresOn"/> (or had nothing to project — a still-learning item with a dated purchase),
    /// so the due date was hard-capped there. The cadence estimates how long stock usually lasts; the
    /// label bounds how long it can. Lets the detail page say why the next-buy date moved.
    public bool DueCappedByExpiration { get; init; }

    // ---- Derived: the two-stream gap (rebuy rhythm vs. burn rate) ----

    /// <summary>Rebuy rhythm minus burn rate in days, when both are known. Positive means you run out this
    /// many days before you typically rebuy (chronic shortage — buy sooner or a bigger size); negative means
    /// you restock that many days before running out (comfortably ahead). Null unless both rhythms exist.</summary>
    public double? RebuyBurnGapDays =>
        RebuyIntervalDays is { } rebuy && BurnRateDays is { } burn ? rebuy - burn : null;

    /// <summary>You chronically run out before rebuying by a margin worth surfacing (not a day of noise) —
    /// the dashboard's "you keep running out of these" flag. Burn rate only exists after ≥2 real outage
    /// cycles, so this never fires on thin data.</summary>
    public bool RunsOutEarly => RebuyBurnGapDays >= RunsOutEarlyThresholdDays;

    private const double RunsOutEarlyThresholdDays = 3;
}
