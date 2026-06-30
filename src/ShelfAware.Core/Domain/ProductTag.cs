namespace ShelfAware.Core.Domain;

/// <summary>
/// A descriptive tag on a product (e.g. "Condiment", "Canned", "Snack") — the second category layer.
/// <see cref="Product.Category"/> is the single store-aisle (drives grocery-list order); tags are
/// many-per-product and power the browsable tag cloud + filtering. One row per product/tag pairing.
/// </summary>
public class ProductTag
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public required string Value { get; set; }
}
