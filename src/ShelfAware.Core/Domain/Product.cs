namespace ShelfAware.Core.Domain;

public class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public Category Category { get; set; }
    public string? DefaultUnit { get; set; }
    public bool IsTracked { get; set; } = true;

    public List<PurchaseEvent> Purchases { get; set; } = [];
    public List<InventorySignal> Signals { get; set; } = [];
}
