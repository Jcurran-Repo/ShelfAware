using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a won't-eat list change: which food, and which DIRECTION it moved.
/// The undo reverses that direction — an add is undone by removing, a remove by re-adding.</summary>
public sealed record ExcludedFoodChangedPayload(bool WasAdded, string Value);

/// <summary>Undo for a won't-eat ("food you won't eat") change. Reverses the direction the action moved:
/// undo an ADD by removing the food (if it's still there), undo a REMOVE by putting it back (if it isn't
/// there again). <see cref="UndoResult.Gone"/> when the reversal would be a no-op — the food is already in
/// the state the undo would put it in (removed by hand, or re-added by hand) — so /history greys it rather
/// than offering a button that does nothing.
/// <para>Matching is case-insensitive in memory (the won't-eat list is tiny, and the add path dedupes
/// case-insensitively), so an add later re-typed with different casing is still recognised as present.</para></summary>
public sealed class ExcludedFoodChangedHandler : UndoHandler<ExcludedFoodChangedPayload>
{
    public override ActivityKind Kind => ActivityKind.ExcludedFoodChanged;

    protected override string Summarize(ExcludedFoodChangedPayload p) =>
        p.WasAdded
            ? $"Added {p.Value} to your won't-eat list"
            : $"Removed {p.Value} from your won't-eat list";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, ExcludedFoodChangedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var existing = (await db.ExcludedFoods.ToListAsync(ct))
            .FirstOrDefault(f => string.Equals(f.Value, p.Value, StringComparison.OrdinalIgnoreCase));

        if (p.WasAdded)
        {
            if (existing is null) return UndoResult.Gone; // removed by hand since — nothing to undo
            db.ExcludedFoods.Remove(existing);
            return UndoResult.Done;
        }

        if (existing is not null) return UndoResult.Gone; // re-added by hand since — already back
        db.ExcludedFoods.Add(new ExcludedFood { Value = p.Value });
        return UndoResult.Done;
    }
}
