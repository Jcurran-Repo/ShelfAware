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

    /// <summary>The user's explicit assertion that they checked every line, making this receipt's
    /// confirmed lines usable as extraction ground truth ("your receipts" on /accuracy). NEVER set by a
    /// machine confirm — an unreviewed receipt as "truth" would let the eval grade extraction against
    /// itself and inflate the scores.</summary>
    public bool VerifiedForEval { get; set; }

    public List<ReceiptLine> Lines { get; set; } = [];
}
