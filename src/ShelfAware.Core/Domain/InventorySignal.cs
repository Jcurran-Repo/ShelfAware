namespace ShelfAware.Core.Domain;

public class InventorySignal
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public DateTimeOffset SignaledAt { get; set; }
    public SignalKind Kind { get; set; }
}
