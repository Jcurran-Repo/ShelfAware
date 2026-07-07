namespace ShelfAware.Core.Domain;

/// <summary>One ordered step of a recipe's cooking method. Added in v2 so read-aloud has real
/// cook-along content — name + blurb + ingredients alone aren't a method.</summary>
public class RecipeStep : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    /// <summary>1-based position in the method.</summary>
    public int Order { get; set; }
    public required string Text { get; set; }
}
