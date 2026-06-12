namespace ShelfAware.Core.Domain;

public class Receipt
{
    public int Id { get; set; }
    public string? Merchant { get; set; }
    public DateOnly? PurchasedAt { get; set; }
    public required string ImagePath { get; set; }
    public string RawModelJson { get; set; } = "";
    public ReceiptStatus Status { get; set; } = ReceiptStatus.PendingReview;

    public List<ReceiptLine> Lines { get; set; } = [];
}
