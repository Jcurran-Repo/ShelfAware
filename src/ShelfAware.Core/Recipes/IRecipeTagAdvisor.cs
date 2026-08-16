namespace ShelfAware.Core.Recipes;

/// <summary>
/// Suggests descriptive tags (meal, cuisine, diet, dish type) for a recipe from its name and
/// ingredients — the AI seed behind the cookbook's tag cloud. It's given the household's known tags so
/// it leans on the existing vocabulary instead of coining near-duplicates. Fails open: a model or
/// network failure returns an empty list, and never blocks saving, importing, or viewing a recipe.
/// </summary>
public interface IRecipeTagAdvisor
{
    Task<IReadOnlyList<string>> SuggestAsync(
        string recipeName,
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<string> knownTags,
        CancellationToken cancellationToken = default);
}
