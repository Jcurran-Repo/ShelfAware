using ShelfAware.Core.Recipes;

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

    // ── Behaviour ───────────────────────────────────────────────────────────────────────────────────
    // Ask the recipe about itself ("are you a variant?", "can I make you with this?") instead of poking
    // at raw fields from every caller. The fuzzy makeability rules stay in IngredientMatcher (the domain
    // service); the recipe just applies them to its own MAIN ingredients.

    /// <summary>True when this recipe is an Adapt-generated variant of another saved recipe, not an original.</summary>
    public bool IsVariant => ParentRecipeId is not null;

    /// <summary>True when this recipe is an adapted variant of <paramref name="original"/>.</summary>
    public bool IsVariantOf(Recipe original) => ParentRecipeId == original.Id;

    /// <summary>The real ingredients that decide makeability (protein/veg/starch); seasonings and staples
    /// are excluded.</summary>
    public IEnumerable<RecipeIngredient> MainIngredients => Ingredients.Where(i => i.IsMain);

    /// <summary>The seasonings, spices, oils, and staples — suggestion-only, never part of the makeable check.</summary>
    public IEnumerable<RecipeIngredient> Seasonings => Ingredients.Where(i => !i.IsMain);

    /// <summary>True when every MAIN ingredient is covered by something on hand (by food family, via each
    /// product's substitute list — not exact name). A recipe with no mains is never makeable.</summary>
    public bool IsMakeableWith(IReadOnlyCollection<PantryProduct> onHand)
    {
        var mains = MainIngredients.ToList();
        return mains.Count > 0 && mains.All(i => i.IsSatisfiedBy(onHand));
    }

    /// <summary>The MAIN ingredients not currently covered by anything on hand — what you'd need to buy.</summary>
    public IEnumerable<RecipeIngredient> MissingMains(IReadOnlyCollection<PantryProduct> onHand) =>
        MainIngredients.Where(i => !i.IsSatisfiedBy(onHand));
}
