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
/// The change-check compares on-hand (exact decimal) and the tracking flag — a re-count to a DIFFERENT
/// value, or a stop/resume, is caught there. A re-attest to the exact SAME value moves only the clock, so
/// on-hand can't see it; the attestation TIMESTAMP catches that — <see cref="ActivityEntry.OccurredAt"/> vs
/// the product's current <c>QuantityCountedAt</c>, the same guard <c>PurchaseAddedHandler</c> makes against
/// a re-count (a DB-to-DB comparison, no JSON round-trip — the payload's own timestamp is never compared).
/// The recording path attests BEFORE it records, so <c>QuantityCountedAt ≤ OccurredAt</c> for this entry's
/// own action and the guard never false-refuses an immediate undo.</summary>
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
            return UndoResult.Superseded; // re-counted / re-toggled to a DIFFERENT value since
        // A later attestation that re-set the SAME value moved only the clock — undoing this older entry
        // would silently revert past it (discard the newer look). The timestamp catches what on-hand can't.
        if (product.QuantityCountedAt is { } counted && counted > entry.OccurredAt)
            return UndoResult.Superseded;

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
