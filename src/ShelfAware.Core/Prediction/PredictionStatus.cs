namespace ShelfAware.Core.Prediction;

/// <summary>Replenishment status for a tracked product (DESIGN.md §6.5).
/// ⚠️ Declaration order IS severity order and is COMPARED (the expiration cap escalates via
/// <c>labelStatus &gt; status</c>) — don't reorder or insert members mid-list.</summary>
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
