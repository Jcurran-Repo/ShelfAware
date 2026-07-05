namespace ShelfAware.Core.Domain;

/// <summary>A recipe the user saved (from an AI suggestion). Its ingredients carry a main/seasoning flag
/// so the "can I make this?" check (Phase 2) judges only real ingredients, never spices/staples.</summary>
public class Recipe
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Blurb { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    /// <summary>Number of times marked "eaten" — the reliability signal for the Phase-2 "Pick for me".</summary>
    public int TimesEaten { get; set; }
    public List<RecipeIngredient> Ingredients { get; set; } = [];
    /// <summary>Ordered cooking method (v2) — the content read-aloud steps through.</summary>
    public List<RecipeStep> Steps { get; set; } = [];
    /// <summary>LLM's rough estimated calories per serving (ballpark, not precise nutrition); null if unknown.</summary>
    public int? EstimatedCaloriesPerServing { get; set; }
    /// <summary>Set when this recipe is an "Adapt"-generated variant of another saved recipe (rewritten to
    /// use what's on hand); null for an original. Variants group under their parent on the Recipes page.</summary>
    public int? ParentRecipeId { get; set; }
}
