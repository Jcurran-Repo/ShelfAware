namespace ShelfAware.Core.Domain;

public class PurchaseEvent
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public DateOnly PurchasedAt { get; set; }
    public decimal Quantity { get; set; } = 1;
    public PurchaseSource Source { get; set; }
    public int? ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }
}
