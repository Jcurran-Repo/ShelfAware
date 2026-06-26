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

    /// Median repurchase interval in days; null when there is too little history.
    public double? MedianIntervalDays { get; init; }

    /// Short statistical explanation for UI transparency, e.g. "bought 5×, ~every 12 days".
    public required string Basis { get; init; }

    /// Active user-statement note, e.g. "Marked out of stock"; null when none. Kept separate from
    /// Basis so the UI can present the prediction and the user's own signal as distinct cues.
    public string? SignalNote { get; init; }

    /// True when an active OutNow signal pins this item to the top of the list (§6.6 / §8).
    public bool Pinned { get; init; }
}
