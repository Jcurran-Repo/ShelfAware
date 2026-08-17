using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a count change (<c>SetQuantityAsync</c> — an absolute attest, a
/// relative "used one", or stop-counting). Carries the pre-action snapshot to restore, the resulting
/// on-hand + tracking to detect a change since, and the id of any OutNow the action filed (an asserted
/// zero), so undo can delete it too.</summary>
public sealed record CountSetPayload(
    int ProductId, string ProductName,
    decimal? OldOnHand, DateTimeOffset? OldCountedAt, bool OldTrackQuantity,
    decimal? NewOnHand, bool NewTrackQuantity,
    int? CreatedSignalId);

/// <summary>Undo for a count change. Restores the pre-action count, its attestation date, and the
/// tracking flag, and deletes the OutNow the action filed if it made one. Refuses
/// <see cref="UndoResult.Superseded"/> if the count or tracking changed since (a re-count, or a later
/// stop/resume — so undoing would clobber it) and <see cref="UndoResult.Gone"/> if the product is gone.
///
/// The change-check compares on-hand (exact decimal) and the tracking flag, NOT the attestation
/// timestamp — comparing a DB-round-tripped <c>DateTimeOffset</c> to a JSON one risks a false mismatch,
/// and on-hand already catches a re-count to a different value. Accepted edge: a re-attest to the exact
/// same count (only the clock moved) isn't detected, so undoing then restores the older clock.</summary>
public sealed class CountSetHandler : UndoHandler<CountSetPayload>
{
    public override ActivityKind Kind => ActivityKind.CountSet;

    protected override string Summarize(CountSetPayload p) =>
        !p.NewTrackQuantity && p.OldTrackQuantity
            ? $"Stopped counting {p.ProductName}"
            : $"Set {p.ProductName} count to {p.NewOnHand ?? 0:0.##}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, CountSetPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(x => x.Id == p.ProductId, ct);
        if (product is null) return UndoResult.Gone;
        if (product.QuantityOnHand != p.NewOnHand || product.TrackQuantity != p.NewTrackQuantity)
            return UndoResult.Superseded; // re-counted / re-toggled since

        product.QuantityOnHand = p.OldOnHand;
        product.QuantityCountedAt = p.OldCountedAt;
        product.TrackQuantity = p.OldTrackQuantity;

        // If the count asserted a zero, the action filed an OutNow — take it back too (if still there).
        if (p.CreatedSignalId is { } sid &&
            await db.InventorySignals.FirstOrDefaultAsync(s => s.Id == sid, ct) is { } signal)
        {
            db.InventorySignals.Remove(signal);
        }
        return UndoResult.Done;
    }
}
