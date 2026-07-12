namespace ShelfAware.Core.Recipes;

/// <summary>
/// Builds the swap bubble-cloud for a recipe ingredient: the user's own products that can stand in for it
/// (their curated "also works as" matrix made visible and clickable) first, then the AI-generated generic
/// forms that aren't already represented by one. Plain code — the LLM only ever supplies the generic forms.
/// </summary>
public static class SwapCloud
{
    /// <summary>Products whose name or curated "also works as" list covers <paramref name="ingredientName"/> —
    /// the concrete things the user owns (or tracks) that this ingredient could be re-written around.
    /// A product that IS the ingredient token-for-token is excluded: swapping to it would just remake the
    /// same recipe. Pass every tracked edible product, not just in-stock — an out-of-stock stand-in is
    /// still a valid swap target (it renders as "grab").</summary>
    public static List<string> CuratedStandIns(string ingredientName, IEnumerable<PantryProduct> products) =>
        products
            .Where(p => !IngredientMatcher.IsSameFood(ingredientName, p.Name) &&
                        IngredientMatcher.IsSatisfied(ingredientName, null, [p]))
            .Select(p => p.Name)
            .ToList();

    /// <summary>Curated stand-ins first, then generated forms not already represented by one — a generated
    /// "chicken tenderloins" is dropped when "Chicken Breast Tenderloins" is in the curated list, so the
    /// concrete product the user owns wins over the generic phrase.</summary>
    public static List<string> Merge(IReadOnlyList<string> curated, IReadOnlyList<string> generated) =>
        [.. curated, .. generated.Where(g => !IngredientMatcher.IsMentionedIn(g, curated))];
}
