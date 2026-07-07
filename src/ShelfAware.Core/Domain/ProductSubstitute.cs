namespace ShelfAware.Core.Domain;

/// <summary>
/// A recipe ingredient this product can stand in for ("also works as") — e.g. "Chicken Breast Tenderloins"
/// also works as "chicken breast" and "chicken cutlet". Powers makeability: a recipe stays specific (real
/// cuts, real cook times) but still goes green when you own a valid substitute. AI-seeded, user-curated.
/// One row per product/phrase pairing (mirrors <see cref="ProductTag"/>).
/// </summary>
public class ProductSubstitute : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public required string Value { get; set; }
}
