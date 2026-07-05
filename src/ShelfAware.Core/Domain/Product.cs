namespace ShelfAware.Core.Domain;

public class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>The store aisle where you'd grab this item — drives grocery-list ordering. The single
    /// primary axis; finer descriptors live in <see cref="Tags"/>.</summary>
    public Category Category { get; set; }
    public string? DefaultUnit { get; set; }
    public bool IsTracked { get; set; } = true;

    public List<PurchaseEvent> Purchases { get; set; } = [];
    public List<InventorySignal> Signals { get; set; } = [];
    /// <summary>Descriptive tags (Condiment, Canned, Snack, …) — the browsable second category layer.</summary>
    public List<ProductTag> Tags { get; set; } = [];
    /// <summary>Recipe ingredients this product can stand in for ("also works as") — drives makeability
    /// without genericizing recipes. AI-seeded, user-curated.</summary>
    public List<ProductSubstitute> Substitutes { get; set; } = [];
}
