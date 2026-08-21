using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Which variant an adapt produced, plus the family + variant names for the summary. Undoing an
/// adapt deletes the variant it created (a variant has no children of its own — re-adapt re-roots under the
/// original — so there's no family to fold in; it does NOT restore any stale variants that adapt replaced).</summary>
public sealed record RecipeAdaptedPayload(int VariantId, string FamilyName, string VariantName);

/// <summary>Undo for adapting a recipe to what's on hand (<c>RecipeAdapter</c>): delete the variant it made.
/// Pristine → a plain undo; cooked / tagged / photographed → WARNS and proceeds only on confirm (same base +
/// guard as <see cref="RecipeSavedHandler"/>).</summary>
public sealed class RecipeAdaptedHandler(IRecipeImageCleanup imageCleanup)
    : RecipeUndoHandler<RecipeAdaptedPayload>(imageCleanup)
{
    public override ActivityKind Kind => ActivityKind.RecipeAdapted;

    protected override string Summarize(RecipeAdaptedPayload p) => $"Adapted {p.FamilyName} → {p.VariantName}";

    protected override int RecipeId(RecipeAdaptedPayload p) => p.VariantId;
}
