namespace ShelfAware.Core.Recipes;

/// <summary>A photo to extract a recipe from (bytes + media type) — a recipe card, a cookbook page, a
/// handwritten note, a screenshot.</summary>
public record RecipePhoto(byte[] Data, string MediaType);

/// <summary>One ingredient as extracted, before review: its name, the free-form amount (or null), and
/// whether it's a main ingredient (vs a seasoning/staple).</summary>
public record ImportedIngredient(string Name, string? Quantity, bool IsMain);

/// <summary>A recipe extracted from a photo or pasted text, before the human reviews and saves it.</summary>
public record ImportedRecipe(
    string Name,
    string? Blurb,
    int? CaloriesPerServing,
    IReadOnlyList<ImportedIngredient> Ingredients,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Tags);

/// <summary>The outcome of an import attempt — the extracted recipe, or a reason none was produced.</summary>
public record RecipeImportResult(bool Success, ImportedRecipe? Recipe, string? Error)
{
    public static RecipeImportResult Ok(ImportedRecipe recipe) => new(true, recipe, null);
    public static RecipeImportResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Extracts a structured recipe from a PHOTO (a recipe card, cookbook page, handwritten note, screenshot)
/// or from pasted TEXT, for the cookbook's import flow. Like the receipt extractor and the shelf-census
/// reader, the result is always REVIEWED before it's saved — never trusted silently — and it reports when
/// no recipe was found rather than inventing one.
/// </summary>
public interface IRecipeImporter
{
    Task<RecipeImportResult> ImportFromImageAsync(RecipePhoto image, CancellationToken cancellationToken = default);
    Task<RecipeImportResult> ImportFromTextAsync(string text, CancellationToken cancellationToken = default);
}
