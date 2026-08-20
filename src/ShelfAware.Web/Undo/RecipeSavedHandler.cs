using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Undo;

/// <summary>The saved recipe's name, for the history line. History-only for v1: a recipe accumulates
/// history (times eaten, dated meal events, adapted variants) and deleting one cascades that away or
/// orphans the variants, so undoing a save is not a clean reversal the way removing a purchase is.</summary>
public sealed record RecipeSavedPayload(string RecipeName);

/// <summary>History-only record of saving a recipe from the suggestions (<c>Recipes.razor</c> Save):
/// recorded, shown greyed on /history, never undone. The 🗑 delete on the recipe card is the manual
/// removal path.</summary>
public sealed class RecipeSavedHandler : HistoryOnlyHandler<RecipeSavedPayload>
{
    public override ActivityKind Kind => ActivityKind.RecipeSaved;

    protected override string Summarize(RecipeSavedPayload p) => $"Saved recipe: {p.RecipeName}";
}
