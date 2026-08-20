using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a start/stop-tracking toggle (the grocery list's Untrack / the
/// dashboard's resume): which product, and its before/after tracked state.</summary>
public sealed record TrackingChangedPayload(int ProductId, bool WasTracked, bool NowTracked, string ProductName);

/// <summary>Undo for a tracking toggle: flip <c>IsTracked</c> back. Refuses
/// <see cref="UndoResult.Superseded"/> if it was toggled again since (so undoing wouldn't restore the
/// state this action changed), and <see cref="UndoResult.Gone"/> if the product is deleted.</summary>
public sealed class TrackingChangedHandler : UndoHandler<TrackingChangedPayload>
{
    public override ActivityKind Kind => ActivityKind.TrackingChanged;

    protected override string Summarize(TrackingChangedPayload p) =>
        p.NowTracked ? $"Resumed tracking {p.ProductName}" : $"Stopped tracking {p.ProductName}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, TrackingChangedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(x => x.Id == p.ProductId, ct);
        if (product is null) return UndoResult.Gone;
        if (product.IsTracked != p.NowTracked) return UndoResult.Superseded; // toggled again since
        product.IsTracked = p.WasTracked;
        return UndoResult.Done;
    }
}
