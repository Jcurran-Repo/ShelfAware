using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Shared base for the recipe save/adapt undo handlers: both DELETE the recipe (and, for a save, its
/// adapted variants), WARN first when it's been built on (<see cref="IUndoConfirmable"/>), and reap the photo
/// file(s) after the commit (<see cref="IUndoAfterCommit"/>). A concrete handler only supplies its
/// <see cref="UndoHandler{TPayload}.Kind"/>, its summary, and which id in its payload names the recipe.
/// <para>⚠️ <see cref="_photosToReap"/> is captured during <see cref="Reverse"/> and consumed by
/// <see cref="IUndoAfterCommit.AfterCommitAsync"/> — the only Peek-safe way to reach files whose rows are
/// gone by commit time. Within one <c>UndoAsync</c> call the sequence is Reverse (capture) → commit →
/// AfterCommit (reap), so the field holds the current undo's files; a Peek runs Reverse (re-capturing) but
/// never AfterCommit, so it deletes nothing, and each Reverse overwrites the field so a stale Peek can't
/// leak into a later undo. Circuit-serialized, so no concurrent Reverse interleaves.</para></summary>
public abstract class RecipeUndoHandler<TPayload>(IRecipeImageCleanup imageCleanup)
    : UndoHandler<TPayload>, IUndoConfirmable, IUndoAfterCommit
{
    private IReadOnlyList<string> _photosToReap = [];

    /// <summary>Which id in the payload names the recipe to delete.</summary>
    protected abstract int RecipeId(TPayload payload);

    async Task<string?> IUndoConfirmable.DestructiveWarningAsync(
        ShelfAwareDbContext db, ActivityEntry entry, CancellationToken ct)
    {
        var recipe = await db.Recipes.FindAsync([RecipeId(Deserialize(entry.PayloadJson))], ct);
        return recipe is null ? null : await RecipeReversal.BuildWarningAsync(db, recipe, ct); // gone → Reverse returns Gone
    }

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, TPayload payload, ActivityEntry entry, CancellationToken ct)
    {
        _photosToReap = []; // clear first, so a Gone/early return leaves nothing for AfterCommit to reap
        var recipe = await db.Recipes.FindAsync([RecipeId(payload)], ct);
        if (recipe is null) return UndoResult.Gone;
        _photosToReap = await RecipeReversal.StageFamilyDeleteAsync(db, recipe, ct);
        return UndoResult.Done;
    }

    Task IUndoAfterCommit.AfterCommitAsync(ActivityEntry entry, CancellationToken ct)
    {
        foreach (var path in _photosToReap) imageCleanup.Delete(path); // best-effort; the rows are already gone
        _photosToReap = [];
        return Task.CompletedTask;
    }
}
