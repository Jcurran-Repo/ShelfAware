namespace ShelfAware.Core.Prediction;

/// <summary>Replenishment status for a tracked product (DESIGN.md §6.5).</summary>
public enum PredictionStatus
{
    /// Fewer than two purchase-equivalent dates — "still learning".
    Unknown,
    /// Comfortably before the predicted run-out date.
    Stocked,
    /// Within the warning window of the due date (or floored here by a RunningLow signal).
    DueSoon,
    /// Past the predicted run-out date (or pinned here by an OutNow signal).
    Overdue
}
