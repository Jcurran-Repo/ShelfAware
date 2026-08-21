using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Which recipe was saved, and its name for the summary. The id is what lets the undo delete it.</summary>
public sealed record RecipeSavedPayload(int RecipeId, string RecipeName);

/// <summary>Undo for saving a recipe from the suggestions (<c>Recipes.razor</c> Save): delete the recipe
/// again — but only while it is still exactly as saved. Refuses <see cref="UndoResult.Superseded"/> once
/// it's been BUILT ON (cooked, adapted into a variant, tagged, or photographed — see
/// <see cref="RecipeReversal"/>), where the manual 🗑 is the explicit removal path; <see cref="UndoResult.Gone"/>
/// if it's already deleted. A pristine recipe has only its ingredients and steps, which cascade cleanly.</summary>
public sealed class RecipeSavedHandler : UndoHandler<RecipeSavedPayload>
{
    public override ActivityKind Kind => ActivityKind.RecipeSaved;

    protected override string Summarize(RecipeSavedPayload p) => $"Saved recipe: {p.RecipeName}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, RecipeSavedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var recipe = await db.Recipes.FindAsync([p.RecipeId], ct);
        if (recipe is null) return UndoResult.Gone;
        if (await RecipeReversal.HasBeenBuiltOnAsync(db, recipe, ct)) return UndoResult.Superseded;
        db.Recipes.Remove(recipe); // ingredients + steps cascade; nothing else exists (the guard ensured it)
        return UndoResult.Done;
    }
}
