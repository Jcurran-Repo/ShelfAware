namespace ShelfAware.Core.Recipes;

/// <summary>
/// Generates recipe ideas from a natural-language request, reasoning over what the user likely has on
/// hand and hard-excluding foods they won't eat. Squarely an LLM job (creative generation + language +
/// constraint reasoning); the availability bookkeeping around it is plain code.
/// </summary>
public interface IRecipeAdvisor
{
    Task<IReadOnlyList<RecipeSuggestion>> SuggestAsync(
        string request,
        IReadOnlyList<string> onHand,
        IReadOnlyList<string> excludedFoods,
        CancellationToken cancellationToken = default);

    /// <summary>Rewrite an existing recipe so it can be cooked with what's on hand — swap main ingredients
    /// the user doesn't have for ones they do that fit the dish, and adjust the steps/cook times to match.
    /// Returns the adapted recipe, or null if the model returned nothing usable.</summary>
    Task<RecipeSuggestion?> AdaptAsync(
        RecipeToAdapt recipe,
        IReadOnlyList<string> onHand,
        IReadOnlyList<string> excludedFoods,
        CancellationToken cancellationToken = default);
}

/// <summary>The existing recipe handed to <see cref="IRecipeAdvisor.AdaptAsync"/>.</summary>
public record RecipeToAdapt(string Name, string? Blurb, IReadOnlyList<AdaptIngredient> Ingredients, IReadOnlyList<string> Steps);

public record AdaptIngredient(string Name, bool IsMain);

public record RecipeSuggestion(
    string Name, string Blurb, IReadOnlyList<SuggestedIngredient> Ingredients, IReadOnlyList<string> Steps,
    int? CaloriesPerServing = null)
{
    /// <summary>Main ingredients the user still needs to buy — what to add to the grocery list.</summary>
    public IEnumerable<SuggestedIngredient> ToGrab => Ingredients.Where(i => i.IsMain && !i.Have);
}

/// <param name="IsMain">True = a real/main ingredient (counts toward makeability); false = seasoning/staple.</param>
/// <param name="MatchedProduct">The existing on-hand product this ingredient maps to (the model picks it
/// from the provided list), or null if the user doesn't have it. Grounded matching, not a self-reported
/// guess — <see cref="Have"/> is derived from it, and it's persisted so the makeability check is plain code.</param>
public record SuggestedIngredient(string Name, bool IsMain, string? MatchedProduct)
{
    public bool Have => MatchedProduct is not null;
}
