using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a receipt line tracked after its confirm: the line and the purchase
/// it recorded, the product it now points at (and whether tracking created it), how much the count moved
/// (0 when a newer count already held the purchase), and the merchant alias it taught, if any.</summary>
public sealed record ReceiptLineTrackedPayload(
    int LineId, int PurchaseId, int ProductId, string ProductName, bool CreatedProduct,
    decimal Quantity, decimal CountMoved, int? AliasId);

/// <summary>Undo for <see cref="ReceiptLineProductService.TrackAsync"/>: the line points at nothing again,
/// its purchase goes, the count gives back exactly what the track added, the alias it taught is untaught,
/// and a product the track created goes too while nothing else has touched it. The same "keep what gained
/// other history" rule as <see cref="ProductCreatedHandler"/> and <c>ReceiptRemovalService</c>.
/// <list type="bullet">
///   <item><see cref="UndoResult.Gone"/> when the purchase is already gone — the receipt was removed, or
///   the product deleted, since.</item>
///   <item><see cref="UndoResult.Superseded"/> when the line points somewhere else now (a merge moved it)
///   or the purchase's quantity was corrected since — reversing by the recorded numbers would clobber
///   that later edit.</item>
///   <item>The count stands down past a recount taken after the track, as every purchase undo does.</item>
///   <item>Re-tracking is not undone: buying an item resumes tracking, and no other purchase undo
///   un-resumes it either.</item>
/// </list></summary>
public sealed class ReceiptLineTrackedHandler : UndoHandler<ReceiptLineTrackedPayload>
{
    public override ActivityKind Kind => ActivityKind.ReceiptLineTracked;

    protected override string Summarize(ReceiptLineTrackedPayload p) => p.CreatedProduct
        ? $"Created {p.ProductName} from a receipt line"
        : $"Tracked a receipt line as {p.ProductName}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, ReceiptLineTrackedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        // Filtered lookups: a foreign household's context loads none of these rows.
        var purchase = await db.PurchaseEvents.Include(x => x.Product)
            .FirstOrDefaultAsync(x => x.Id == p.PurchaseId, ct);
        if (purchase?.Product is not { } product) return UndoResult.Gone;
        var line = await db.ReceiptLines.FirstOrDefaultAsync(x => x.Id == p.LineId, ct);
        if (line is null) return UndoResult.Gone;
        if (line.ProductId != p.ProductId || purchase.ProductId != p.ProductId || purchase.Quantity != p.Quantity)
            return UndoResult.Superseded;

        if (p.CountMoved > 0 && !(product.QuantityCountedAt is { } counted && counted > entry.OccurredAt))
            StockLedger.Remove(product, p.CountMoved);

        db.PurchaseEvents.Remove(purchase);
        line.ProductId = null;

        if (p.AliasId is { } aliasId
            && await db.ProductAliases.FirstOrDefaultAsync(a => a.Id == aliasId, ct) is { } alias
            && alias.ProductId == p.ProductId)
            db.ProductAliases.Remove(alias);

        if (p.CreatedProduct)
        {
            // Anything but this one purchase and this one line is history the household gave it since.
            var hasOtherHistory =
                product.QuantityCountedAt is not null ||
                await db.PurchaseEvents.AnyAsync(x => x.ProductId == product.Id && x.Id != purchase.Id, ct) ||
                await db.InventorySignals.AnyAsync(s => s.ProductId == product.Id, ct) ||
                await db.ReceiptLines.AnyAsync(l => l.ProductId == product.Id && l.Id != line.Id, ct);
            if (!hasOtherHistory) db.Products.Remove(product); // cascades its tags and aliases
        }
        return UndoResult.Done;
    }
}
