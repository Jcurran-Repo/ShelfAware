using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Shared logic for undoing a recipe save or adapt — the warning to show first, and the delete
/// itself. Undoing DELETES the recipe, and once a recipe has been BUILT ON (cooked, adapted into variants,
/// tagged, or photographed) deleting it loses that — so instead of REFUSING, the undo WARNS (via
/// <see cref="BuildWarningAsync"/>) and proceeds only on an explicit confirm (Jordan's call). One definition
/// so both recipe handlers warn and delete identically.</summary>
public static class RecipeReversal
{
    /// <summary>The warning naming what this recipe has picked up since it was saved, or NULL when it's still
    /// pristine (only ingredients + steps) — in which case the undo is a plain delete needing no confirm.</summary>
    public static async Task<string?> BuildWarningAsync(
        ShelfAwareDbContext db, Recipe recipe, CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (recipe.TimesEaten > 0)
            parts.Add($"cooked it {recipe.TimesEaten}×");
        else if (await db.MealEvents.AnyAsync(m => m.RecipeId == recipe.Id, ct))
            parts.Add("cooked it");

        var variants = await db.Recipes.CountAsync(r => r.ParentRecipeId == recipe.Id, ct);
        if (variants > 0)
            parts.Add($"adapted it into {variants} version{(variants == 1 ? "" : "s")}");

        if (await db.RecipeTags.AnyAsync(t => t.RecipeId == recipe.Id, ct))
            parts.Add("tagged it");
        if (!string.IsNullOrEmpty(recipe.ImagePath))
            parts.Add("added a photo");

        if (parts.Count == 0) return null;

        var variantNote = variants > 0 ? $" (including the adapted version{(variants == 1 ? "" : "s")})" : "";
        return $"Since you saved this you've {Join(parts)}. Undoing permanently deletes the recipe{variantNote} " +
               "and everything above. Delete anyway?";
    }

    /// <summary>Stage the delete of the recipe AND every variant adapted from it (Jordan's call — a variant
    /// has none of its own, since re-adapt re-roots, so for an adapted recipe this deletes just it). Returns
    /// the photo files to reap AFTER the commit — captured here, before the rows are gone, because the undo
    /// service can't read them once deleted. Stages only; the caller (the service) commits.</summary>
    public static async Task<IReadOnlyList<string>> StageFamilyDeleteAsync(
        ShelfAwareDbContext db, Recipe recipe, CancellationToken ct = default)
    {
        var variants = await db.Recipes.Where(r => r.ParentRecipeId == recipe.Id).ToListAsync(ct);
        var family = new List<Recipe>(variants) { recipe };
        var photos = family.Where(r => !string.IsNullOrEmpty(r.ImagePath)).Select(r => r.ImagePath!).ToList();
        db.Recipes.RemoveRange(family); // ingredients + steps + tags + meal events cascade with each
        return photos;
    }

    // "cooked it 3×" / "cooked it 3× and tagged it" / "cooked it 3×, adapted it into 2 versions, and tagged it".
    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => $"{string.Join(", ", parts.Take(parts.Count - 1))}, and {parts[^1]}",
    };
}
