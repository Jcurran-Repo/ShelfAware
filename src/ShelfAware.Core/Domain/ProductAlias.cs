namespace ShelfAware.Core.Domain;

public class ProductAlias : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public required string Merchant { get; set; }
    public required string RawText { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>The receipt whose human confirm taught (or last RE-pointed) this pairing, or null
    /// (taught before 2026-07-22). Provenance for "remove this receipt": only the teacher's removal
    /// un-teaches the alias. A confirm that re-walks the pairing without changing it is not a new
    /// teacher — a duplicate upload must not inherit credit for a lesson an earlier receipt taught.
    /// A breadcrumb, not an FK (no navigation): the receipt may be gone while the alias lives on.</summary>
    public int? TaughtByReceiptId { get; set; }
}
