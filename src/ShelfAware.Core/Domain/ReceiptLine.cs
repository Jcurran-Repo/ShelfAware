namespace ShelfAware.Core.Domain;

public class ReceiptLine
{
    public int Id { get; set; }
    public int ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }
    public required string RawText { get; set; }
    public required string NormalizedName { get; set; }
    public string? Brand { get; set; }
    /// <summary>Package size on this line (e.g. "1 gal", "12 ct"), or null. Mirrors
    /// <see cref="PurchaseEvent.Size"/> (as Brand is mirrored on both) so a confirmed line's price
    /// can be attributed to the size that was bought — the recommended-size cost estimate.</summary>
    public string? Size { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal? UnitPrice { get; set; }
    public Category Category { get; set; }
    public decimal Confidence { get; set; }
    public int? ProductId { get; set; }
    public Product? Product { get; set; }
}
