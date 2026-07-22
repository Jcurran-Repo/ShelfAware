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
    /// <summary>HISTORICAL: the inbox file name this receipt was auto-imported from, back when the
    /// folder-import feature existed (retired 2026-07-22 — uploads are the one way in now). Nothing
    /// writes or reads it; the column stays because dropping one is a structural SQLite rebuild that
    /// pre-existing rows aren't worth.</summary>
    public string? SourceFile { get; set; }

    /// <summary>The user's explicit assertion that they checked every line, making this receipt's
    /// confirmed lines usable as extraction ground truth ("your receipts" on /accuracy). NEVER set by a
    /// machine confirm — an unreviewed receipt as "truth" would let the eval grade extraction against
    /// itself and inflate the scores.</summary>
    public bool VerifiedForEval { get; set; }

    public List<ReceiptLine> Lines { get; set; } = [];
}
