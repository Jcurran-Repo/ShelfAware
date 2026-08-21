using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>The name of a grocery-list extra that was removed. The undo re-adds it (a fresh row, matched by
/// name — the old id is gone), so the name is all it needs.</summary>
public sealed record GroceryExtraRemovedPayload(string Name);

/// <summary>Undo for removing a manual grocery-list extra: put it back, unless it's already back (re-added
/// by hand since → <see cref="UndoResult.Gone"/>, a no-op the /history page greys rather than a duplicate
/// write). The symmetric counterpart to <c>GroceryExtrasAddedHandler</c>.</summary>
public sealed class GroceryExtraRemovedHandler : UndoHandler<GroceryExtraRemovedPayload>
{
    public override ActivityKind Kind => ActivityKind.GroceryExtraRemoved;

    protected override string Summarize(GroceryExtraRemovedPayload p) => $"Removed {p.Name} from the grocery list";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, GroceryExtraRemovedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var alreadyBack = (await db.GroceryExtras.ToListAsync(ct))
            .Any(e => string.Equals(e.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        if (alreadyBack) return UndoResult.Gone;
        db.GroceryExtras.Add(new GroceryExtra { Name = p.Name });
        return UndoResult.Done;
    }
}
