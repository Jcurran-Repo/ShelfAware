namespace ShelfAware.Core.Domain;

public class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Package size (e.g. "1 gal", "12 ct") — part of the product's identity, so a gallon and a
    /// half-gallon of the same item are distinct products. Null when the item has no meaningful size.</summary>
    public string? Size { get; set; }
    public Category Category { get; set; }
    public string? DefaultUnit { get; set; }
    public bool IsTracked { get; set; } = true;

    public List<PurchaseEvent> Purchases { get; set; } = [];
    public List<InventorySignal> Signals { get; set; } = [];
}
