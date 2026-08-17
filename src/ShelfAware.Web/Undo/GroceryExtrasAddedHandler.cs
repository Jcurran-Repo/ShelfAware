using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for manual grocery-list extras: the exact names this action added
/// (never ones already on the list).</summary>
public sealed record GroceryExtrasAddedPayload(IReadOnlyList<string> Names);

/// <summary>Undo for added grocery extras: remove exactly the ones this action added that are still on
/// the list. <see cref="UndoResult.Gone"/> when none remain.</summary>
public sealed class GroceryExtrasAddedHandler : UndoHandler<GroceryExtrasAddedPayload>
{
    public override ActivityKind Kind => ActivityKind.GroceryExtrasAdded;

    protected override string Summarize(GroceryExtrasAddedPayload p) =>
        $"Added to list: {string.Join(", ", p.Names)}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, GroceryExtrasAddedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var extras = await db.GroceryExtras.Where(e => p.Names.Contains(e.Name)).ToListAsync(ct);
        if (extras.Count == 0) return UndoResult.Gone;
        db.GroceryExtras.RemoveRange(extras);
        return UndoResult.Done;
    }
}
