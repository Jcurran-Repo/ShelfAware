using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Which variant an adapt produced, plus the family + variant names for the summary. Undoing an
/// adapt removes the variant it created (it does NOT restore any stale variants that adapt replaced — those
/// were superseded, and rebuilding them would need their full pre-state, DESIGN.md's bucket-2 territory).</summary>
public sealed record RecipeAdaptedPayload(int VariantId, string FamilyName, string VariantName);

/// <summary>Undo for adapting a recipe to what's on hand (<c>RecipeAdapter</c>): delete the variant it made,
/// while that variant is still exactly as created. Refuses <see cref="UndoResult.Superseded"/> once it's been
/// BUILT ON (cooked, tagged, photographed — a variant can't have child variants, re-adapt re-roots under the
/// original); <see cref="UndoResult.Gone"/> if it's already deleted.</summary>
public sealed class RecipeAdaptedHandler : UndoHandler<RecipeAdaptedPayload>
{
    public override ActivityKind Kind => ActivityKind.RecipeAdapted;

    protected override string Summarize(RecipeAdaptedPayload p) => $"Adapted {p.FamilyName} → {p.VariantName}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, RecipeAdaptedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var variant = await db.Recipes.FindAsync([p.VariantId], ct);
        if (variant is null) return UndoResult.Gone;
        if (await RecipeReversal.HasBeenBuiltOnAsync(db, variant, ct)) return UndoResult.Superseded;
        db.Recipes.Remove(variant); // ingredients + steps cascade
        return UndoResult.Done;
    }
}
