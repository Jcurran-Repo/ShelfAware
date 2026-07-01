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
}

public record RecipeSuggestion(string Name, string Blurb, IReadOnlyList<SuggestedIngredient> Ingredients)
{
    /// <summary>Main ingredients the user still needs to buy — what to add to the grocery list.</summary>
    public IEnumerable<SuggestedIngredient> ToGrab => Ingredients.Where(i => i.IsMain && !i.Have);
}

/// <param name="IsMain">True = a real/main ingredient (counts toward makeability); false = seasoning/staple.</param>
/// <param name="Have">Whether the user likely already has it.</param>
public record SuggestedIngredient(string Name, bool IsMain, bool Have);
