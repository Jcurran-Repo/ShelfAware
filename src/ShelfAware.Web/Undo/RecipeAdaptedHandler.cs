using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Undo;

/// <summary>The recipe family that was adapted and the variant it produced, for the history line.
/// History-only for the same reason as <see cref="RecipeSavedPayload"/>: the variant is a saved recipe
/// that can be eaten and re-adapted, so deleting it on undo is not a clean reversal.</summary>
public sealed record RecipeAdaptedPayload(string FamilyName, string VariantName);

/// <summary>History-only record of adapting a recipe to what's on hand (<c>RecipeAdapter</c>): recorded,
/// shown greyed on /history, never undone.</summary>
public sealed class RecipeAdaptedHandler : HistoryOnlyHandler<RecipeAdaptedPayload>
{
    public override ActivityKind Kind => ActivityKind.RecipeAdapted;

    protected override string Summarize(RecipeAdaptedPayload p) => $"Adapted {p.FamilyName} → {p.VariantName}";
}
