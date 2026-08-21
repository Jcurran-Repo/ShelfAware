using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Which recipe was saved, and its name for the summary. The id is what lets the undo delete it.</summary>
public sealed record RecipeSavedPayload(int RecipeId, string RecipeName);

/// <summary>Undo for saving a recipe from the suggestions (<c>Recipes.razor</c> Save): delete it again. If
/// it's still pristine that's a plain undo; if it's been cooked / adapted / tagged / photographed the undo
/// WARNS (naming what would be lost, incl. the adapted versions it also deletes) and proceeds only on an
/// explicit confirm — see <see cref="RecipeUndoHandler{TPayload}"/> and <see cref="RecipeReversal"/>.</summary>
public sealed class RecipeSavedHandler(IRecipeImageCleanup imageCleanup)
    : RecipeUndoHandler<RecipeSavedPayload>(imageCleanup)
{
    public override ActivityKind Kind => ActivityKind.RecipeSaved;

    protected override string Summarize(RecipeSavedPayload p) => $"Saved recipe: {p.RecipeName}";

    protected override int RecipeId(RecipeSavedPayload p) => p.RecipeId;
}
