namespace ShelfAware.Core.Domain;

public class RecipeIngredient
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public required string Name { get; set; }
    /// <summary>True for real/main ingredients (protein, veg, starch) that decide makeability; false for
    /// seasonings, spices, oils, and pantry staples (suggestion-only, excluded from the makeable check).</summary>
    public bool IsMain { get; set; }
}
