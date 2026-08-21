using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>The shared guard for undoing a recipe save or adapt. Undoing means DELETING the recipe, and a
/// recipe is only safe to delete while it is still exactly as it was created — the moment it has been
/// BUILT ON, deleting it would lose or orphan that work, so the undo refuses (<see cref="UndoResult.Superseded"/>)
/// and the manual 🗑 stays the explicit removal path (which the user chose, and which is expected to be
/// destructive). "Built on" = cooked (<see cref="Recipe.TimesEaten"/> or a dated <c>MealEvent</c>), adapted
/// (a child variant hangs off it — deleting the parent would orphan them via the nullable self-FK), tagged,
/// or given a photo. One definition so both recipe handlers refuse on exactly the same conditions.</summary>
public static class RecipeReversal
{
    public static async Task<bool> HasBeenBuiltOnAsync(
        ShelfAwareDbContext db, Recipe recipe, CancellationToken ct = default)
    {
        if (recipe.TimesEaten > 0) return true;
        if (!string.IsNullOrEmpty(recipe.ImagePath)) return true;
        if (await db.MealEvents.AnyAsync(m => m.RecipeId == recipe.Id, ct)) return true;
        if (await db.Recipes.AnyAsync(r => r.ParentRecipeId == recipe.Id, ct)) return true;
        if (await db.RecipeTags.AnyAsync(t => t.RecipeId == recipe.Id, ct)) return true;
        return false;
    }
}
