namespace ShelfAware.Core.Domain;

public class Receipt : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public string? Merchant { get; set; }
    public DateOnly? PurchasedAt { get; set; }
    public required string ImagePath { get; set; }
    public string RawModelJson { get; set; } = "";
    public ReceiptStatus Status { get; set; } = ReceiptStatus.PendingReview;
    /// <summary>Inbox item id (e.g. file name) this receipt was auto-imported from — so a folder scan never
    /// double-imports the same file. Null for manually uploaded receipts.</summary>
    public string? SourceFile { get; set; }

    public List<ReceiptLine> Lines { get; set; } = [];
}
