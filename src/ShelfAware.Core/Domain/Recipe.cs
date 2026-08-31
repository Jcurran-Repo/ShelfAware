using ShelfAware.Core.Recipes;

namespace ShelfAware.Core.Domain;

/// <summary>A recipe the user saved (from an AI suggestion). Its ingredients carry a main/seasoning flag
/// so the "can I make this?" check (Phase 2) judges only real ingredients, never spices/staples.</summary>
public class Recipe : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public required string Name { get; set; }
    public string? Blurb { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    /// <summary>Number of times marked "eaten" — the reliability signal for the Phase-2 "Pick for me".</summary>
    public int TimesEaten { get; set; }
    public List<RecipeIngredient> Ingredients { get; set; } = [];
    /// <summary>Ordered cooking method (v2) — the content read-aloud steps through.</summary>
    public List<RecipeStep> Steps { get; set; } = [];
    /// <summary>Descriptive tags (meal, cuisine, diet, dish type) powering the cookbook's tag cloud and
    /// filter — the recipe-side analogue of <see cref="Product.Tags"/>. AI-seeded at save/import,
    /// user-editable. Deleted with the recipe (EF cascade, like Ingredients/Steps).</summary>
    public List<RecipeTag> Tags { get; set; } = [];
    /// <summary>Relative path (forward-slash, under the data dir) to this recipe's photo, or null. Owned by
    /// <c>RecipeImageStorage</c> and served household-scoped via <c>/api/recipe-image/{id}</c>. The GUID
    /// filename changes on every replace, so the served URL busts the browser cache.</summary>
    public string? ImagePath { get; set; }
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

    /// <summary>How makeable this recipe is with what's on hand: <see cref="Makeability.Ready"/> (every main
    /// is food you own), <see cref="Makeability.NeedsSwap"/> (every main is covered but ≥1 only by a declared
    /// stand-in, so Adapt should rebuild the steps — "also works as" means you'll eat it, not that it cooks
    /// the same), or <see cref="Makeability.Missing"/> (a main is uncovered, or the recipe has no mains).
    /// <see cref="IsMakeableWith"/> is defined in terms of this, so the badge and the check can't disagree.</summary>
    public Makeability MakeabilityWith(IReadOnlyCollection<PantryProduct> onHand)
    {
        var mains = MainIngredients.ToList();
        if (mains.Count == 0) return Makeability.Missing;
        var coverage = mains.Select(i => i.CoverageBy(onHand)).ToList();
        if (coverage.Any(c => c == IngredientCoverage.None)) return Makeability.Missing;
        return coverage.Any(c => c == IngredientCoverage.Substitute) ? Makeability.NeedsSwap : Makeability.Ready;
    }

    /// <summary>True when every MAIN ingredient is covered by something on hand (by food family, via each
    /// product's substitute list — not exact name), whether directly or by a stand-in. A recipe with no
    /// mains is never makeable. For the three-way detail — including "makeable, but needs a swap" — use
    /// <see cref="MakeabilityWith"/>.</summary>
    public bool IsMakeableWith(IReadOnlyCollection<PantryProduct> onHand) =>
        MakeabilityWith(onHand) != Makeability.Missing;

    /// <summary>The MAIN ingredients not currently covered by anything on hand — what you'd need to buy.</summary>
    public IEnumerable<RecipeIngredient> MissingMains(IReadOnlyCollection<PantryProduct> onHand) =>
        MainIngredients.Where(i => !i.IsSatisfiedBy(onHand));

    /// <summary>True when an ingredient of this recipe is grounded to the named catalog product — its
    /// save-time <see cref="RecipeIngredient.MatchedProduct"/>. This is the single definition of "recipes
    /// that use X" that both the Recipes page's <c>?uses=</c> filter and the cookbook's product filter ask,
    /// so the two surfaces can't drift on what "uses" means. It matches the grounded product NAME only —
    /// deliberately narrower than the food-family makeability rule: it answers "which product did this
    /// recipe map this ingredient to", not "what could stand in for it".</summary>
    public bool Uses(string productName) =>
        Ingredients.Any(i => string.Equals(i.MatchedProduct, productName, StringComparison.OrdinalIgnoreCase));
}
