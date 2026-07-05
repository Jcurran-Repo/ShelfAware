namespace ShelfAware.Core.Recipes;

/// <summary>
/// Suggests the recipe ingredients a product can stand in for ("also works as"), so recipe makeability can
/// stay specific (real cuts, real cook times) yet still go green on a valid stand-in. Language
/// understanding — what's genuinely interchangeable in cooking — is exactly the LLM's job. Fails soft:
/// returns an empty list on any error so the product page never breaks.
/// </summary>
public interface IProductSubstituteAdvisor
{
    /// <param name="productName">The catalog product, e.g. "Chicken Breast Tenderloins".</param>
    /// <param name="category">Its store aisle, for context (e.g. "Meat", "Produce").</param>
    /// <returns>Lowercase recipe-ingredient phrases the product can replace (e.g. "chicken breast",
    /// "chicken cutlet"), realistic swaps only; empty when there are none.</returns>
    Task<IReadOnlyList<string>> SuggestAsync(string productName, string category, CancellationToken cancellationToken = default);
}
