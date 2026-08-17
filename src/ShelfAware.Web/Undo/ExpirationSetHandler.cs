using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>One affected purchase and the expiration date it carried before the change.</summary>
public sealed record ExpirationRow(int PurchaseId, DateOnly? OldDate);

/// <summary>Reverse-and-check data for an expiration change. The date lands on every latest-day purchase
/// at once, so this carries the per-row old dates to restore and the new date to detect a change since.</summary>
public sealed record ExpirationSetPayload(
    int ProductId, string ProductName, DateOnly? NewDate, IReadOnlyList<ExpirationRow> Rows);

/// <summary>Undo for an expiration change: restore each affected purchase's old date. All-or-nothing —
/// refuses <see cref="UndoResult.Superseded"/> if any affected row was re-dated or deleted since (so it
/// never half-reverts), and <see cref="UndoResult.Gone"/> if every affected row is gone.</summary>
public sealed class ExpirationSetHandler : UndoHandler<ExpirationSetPayload>
{
    public override ActivityKind Kind => ActivityKind.ExpirationSet;

    protected override string Summarize(ExpirationSetPayload p) =>
        p.NewDate is { } d ? $"Set {p.ProductName} expiration to {d:MMM d, yyyy}" : $"Cleared {p.ProductName}'s expiration";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, ExpirationSetPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var ids = p.Rows.Select(r => r.PurchaseId).ToList();
        var purchases = await db.PurchaseEvents.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (purchases.Count == 0) return UndoResult.Gone;                       // all affected rows gone
        if (purchases.Count != p.Rows.Count) return UndoResult.Superseded;      // some deleted since
        if (purchases.Any(x => x.ExpirationDate != p.NewDate)) return UndoResult.Superseded; // some re-dated since

        var oldById = p.Rows.ToDictionary(r => r.PurchaseId, r => r.OldDate);
        foreach (var x in purchases) x.ExpirationDate = oldById[x.Id];
        return UndoResult.Done;
    }
}
