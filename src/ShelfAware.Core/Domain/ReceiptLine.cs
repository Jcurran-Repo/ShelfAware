namespace ShelfAware.Core.Domain;

public class ReceiptLine : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public int ReceiptId { get; set; }
    public Receipt? Receipt { get; set; }
    public required string RawText { get; set; }
    public required string NormalizedName { get; set; }
    public string? Brand { get; set; }
    /// <summary>Package size on this line (e.g. "1 gal", "12 ct"), or null. Mirrors
    /// <see cref="PurchaseEvent.Size"/> (as Brand is mirrored on both) so a confirmed line's price
    /// can be attributed to the size that was bought — the recommended-size cost estimate.</summary>
    public string? Size { get; set; }
    /// <summary>Flavor/varietal on this line (e.g. "Strawberry", "Gala"), or null. Mirrors
    /// <see cref="PurchaseEvent.Variety"/> the way Brand and Size do — metadata, not identity, so
    /// every flavor of an item rolls up into one product and one cadence.</summary>
    public string? Variety { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal? UnitPrice { get; set; }
    public Category Category { get; set; }
    public decimal Confidence { get; set; }
    public int? ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>JSON array of the descriptive tags extracted (then reviewed) for this line, or null.
    /// Persisted so a receipt queued for review keeps its tags — they used to live only in memory and
    /// were lost when the auto-importer queued a receipt.</summary>
    public string? TagsJson { get; set; }
    /// <summary>Exact existing-product name the model suggested for this line, or null. Persisted so
    /// the review pre-fill's trust order (alias → model suggestion → matcher) survives a queued receipt.</summary>
    public string? SuggestedProduct { get; set; }
}
