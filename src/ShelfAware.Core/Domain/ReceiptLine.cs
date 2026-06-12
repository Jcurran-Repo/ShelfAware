namespace ShelfAware.Core.Domain;

public class ReceiptLine
{
    public int Id { get; set; }
    public int ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }
    public required string RawText { get; set; }
    public required string NormalizedName { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal? UnitPrice { get; set; }
    public Category Category { get; set; }
    public decimal Confidence { get; set; }
    public int? ProductId { get; set; }
    public Product? Product { get; set; }
}
