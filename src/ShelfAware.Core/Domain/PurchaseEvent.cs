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
    /// <summary>The label's expiration/best-by date for THIS purchase, or null when untracked — always
    /// human-entered (receipts don't print it; extraction never fills it). Per-purchase like Brand/Size/
    /// Variety: two jugs bought two weeks apart carry two different dates, and only the LATEST purchase's
    /// date can mark the item out (rebuying supersedes the old jug). Never feeds either cadence rhythm —
    /// it's a label fact, not buying behavior.</summary>
    public DateOnly? ExpirationDate { get; set; }
    public PurchaseSource Source { get; set; }
    public int? ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }
}
