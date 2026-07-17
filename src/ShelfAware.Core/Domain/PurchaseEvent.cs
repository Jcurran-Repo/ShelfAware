namespace ShelfAware.Core.Domain;

public class PurchaseEvent : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public DateOnly PurchasedAt { get; set; }
    public decimal Quantity { get; set; } = 1;
    /// <summary>Brand bought on this occasion (e.g. "Great Value"), or null if unbranded/unknown.
    /// The product is the brand-agnostic item; brand is tracked per purchase.</summary>
    public string? Brand { get; set; }
    /// <summary>Package size bought on this occasion (e.g. "1 gal", "12 ct"), or null. Metadata, not
    /// identity — different sizes roll up into one product; the predictor uses the dominant size.</summary>
    public string? Size { get; set; }
    /// <summary>Flavor/varietal bought on this occasion (e.g. "Strawberry" drink mix, "Gala" apples),
    /// or null. Metadata like Brand and Size: all varieties roll up into one product and the cadence
    /// is the item's collectively — the product page splits purchases out by variety.</summary>
    public string? Variety { get; set; }
    public PurchaseSource Source { get; set; }
    public int? ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }
}
