using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a purchase-quantity correction: which purchase, and the
/// before/after quantities (to restore the old value, move the count back by the same difference, and
/// detect a further edit since).</summary>
public sealed record PurchaseQuantityEditedPayload(
    int PurchaseId, decimal OldQuantity, decimal NewQuantity, string ProductName);

/// <summary>Undo for a purchase-quantity correction. Restores the old quantity and moves the on-hand
/// count back by exactly the difference the edit applied, mirroring PurchaseAddedHandler's count logic:
/// the count reversal stands down if the item was re-attested after the edit (a human look supersedes
/// the arithmetic). Refuses <see cref="UndoResult.Superseded"/> if the quantity was edited again since
/// (current no longer matches what this edit set), and <see cref="UndoResult.Gone"/> if the purchase is
/// deleted.</summary>
public sealed class PurchaseQuantityEditedHandler : UndoHandler<PurchaseQuantityEditedPayload>
{
    public override ActivityKind Kind => ActivityKind.PurchaseQuantityEdited;

    protected override string Summarize(PurchaseQuantityEditedPayload p) =>
        $"Changed {p.ProductName} quantity to {p.NewQuantity:0.##}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, PurchaseQuantityEditedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var purchase = await db.PurchaseEvents.Include(x => x.Product)
            .FirstOrDefaultAsync(x => x.Id == p.PurchaseId, ct);
        if (purchase is null) return UndoResult.Gone;
        if (purchase.Quantity != p.NewQuantity) return UndoResult.Superseded; // edited again since

        if (purchase.Product is { } product &&
            !(product.QuantityCountedAt is { } counted && counted > entry.OccurredAt))
        {
            StockLedger.Add(product, p.OldQuantity - p.NewQuantity); // move the count back to before the edit
        }
        purchase.Quantity = p.OldQuantity;
        return UndoResult.Done;
    }
}
