namespace ShelfAware.Core.Domain;

/// <summary>Why a receipt line's quantity looks like a misread PACK COUNT rather than a real number of
/// packages bought — a soft, deterministic "we might have oopsied" signal, stamped on the confirmed
/// <see cref="ReceiptLine"/> and computed by <see cref="Ingest.QuantityAnomaly"/>. Never blocks a
/// confirm; it only surfaces the line for a glance. See that class for the reasoning.</summary>
public enum QuantityFlag
{
    /// <summary>Nothing suspicious — the ordinary case, and the stored default.</summary>
    None,

    /// <summary>The size's own pack count equals the quantity — "12 ct" AND quantity 12. The count is in
    /// both fields, so it was almost certainly read into Quantity too. You bought one 12-pack, not twelve.</summary>
    SizeMatchesQuantity,

    /// <summary>A count-shaped quantity with NO size, on a product that USUALLY carries one — the pack
    /// size most likely landed in Quantity and left Size empty.</summary>
    MissingUsualSize,
}
