using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>The reverse-and-check data for an "add a purchase" action: which purchase to delete, the
/// quantity it added (both to word the summary and to take back off the on-hand count), and the product
/// name + date for the summary.</summary>
public sealed record PurchaseAddedPayload(int PurchaseId, decimal Quantity, string ProductName, DateOnly PurchasedAt);

/// <summary>Undo for a recorded purchase — the dashboard's "Bought today" and the chat <c>add_purchase</c>
/// tool are one action with one handler (the <see cref="ActivityEntry.Source"/> tells them apart).
/// Deletes the created <c>PurchaseEvent</c> and takes its quantity back off the on-hand count, mirroring
/// <c>ReceiptRemovalService</c>:
/// <list type="bullet">
///   <item>The count reversal STANDS DOWN if the count was re-attested after the buy — a human look at
///   the shelf supersedes the arithmetic (the exact guard a receipt removal makes against its confirm
///   time; here the reference is <see cref="ActivityEntry.OccurredAt"/>).</item>
///   <item>Re-tracking is deliberately NOT undone — buying an untracked item resumes tracking, and
///   <c>ReceiptRemovalService</c> leaves that alone too; the item is one the household clearly wants.</item>
/// </list>
/// Precondition-checked: <see cref="UndoResult.Superseded"/> if the purchase's quantity was corrected
/// since (a later action changed the same row — reversing by the recorded quantity would clobber it),
/// and <see cref="UndoResult.Gone"/> if the purchase has already been deleted by another path.</summary>
public sealed class PurchaseAddedHandler : UndoHandler<PurchaseAddedPayload>
{
    public override ActivityKind Kind => ActivityKind.PurchaseAdded;

    protected override string Summarize(PurchaseAddedPayload p) =>
        $"Bought {p.Quantity:0.##} × {p.ProductName}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, PurchaseAddedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        // Filtered lookup: a foreign household's context can't load this row, so the undo can only ever
        // reach its own household's purchase (pinned by the isolation test).
        var purchase = await db.PurchaseEvents
            .Include(x => x.Product)
            .FirstOrDefaultAsync(x => x.Id == p.PurchaseId, ct);
        if (purchase is null) return UndoResult.Gone;                     // already deleted by another path
        if (purchase.Quantity != p.Quantity) return UndoResult.Superseded; // its quantity was corrected since

        // Take the stock back exactly as ReceiptRemovalService does — unless a human re-counted this item
        // AFTER the buy, in which case that attestation is the truth and the arithmetic must not touch it.
        // (StockLedger.Remove is itself a no-op for an untracked/uncounted product, so this is always safe.)
        if (purchase.Product is { } product &&
            !(product.QuantityCountedAt is { } counted && counted > entry.OccurredAt))
        {
            StockLedger.Remove(product, p.Quantity);
        }

        db.PurchaseEvents.Remove(purchase);
        return UndoResult.Done; // ActivityLogService commits this removal + the UndoneAt stamp together
    }
}
