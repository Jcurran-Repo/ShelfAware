namespace ShelfAware.Core.Recipes;

/// <summary>
/// Suggests interchangeable FORMS of a recipe ingredient — the swap options shown in the bubble cloud
/// ("chicken breast" → chicken thighs, chicken tenderloins, chicken cutlets, …). Language understanding
/// (what genuinely swaps for what in cooking) is the LLM's job; the result is cached on the ingredient so
/// re-opening the cloud costs nothing. Fails soft: returns empty on any error.
/// </summary>
public interface IIngredientAlternativesAdvisor
{
    /// <returns>Lowercase alternate-form phrases the ingredient could be swapped for (realistic swaps
    /// only — different cuts/forms of the same food or close stand-ins, not wildly different foods);
    /// does NOT repeat the ingredient itself. Empty when there are no meaningful swaps.</returns>
    Task<IReadOnlyList<string>> SuggestAsync(string ingredientName, CancellationToken cancellationToken = default);
}
