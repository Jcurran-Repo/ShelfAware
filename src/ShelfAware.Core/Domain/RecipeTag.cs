namespace ShelfAware.Core.Domain;

/// <summary>
/// A descriptive tag on a recipe (e.g. "Dinner", "Italian", "Vegetarian", "Quick") — the browsable
/// second axis the cookbook filters by, the recipe-side analogue of <see cref="ProductTag"/>. Many
/// per recipe; one row per recipe/tag pairing. AI-seeded at save/import and user-editable.
/// </summary>
public class RecipeTag : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public required string Value { get; set; }
}
