using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Data;

/// <summary>
/// The inverse of <see cref="ReceiptConfirmationService"/>: removes a receipt AND everything its
/// confirm did — one transaction, so a failure removes nothing. Exists because a duplicate upload
/// records duplicate purchases (uploads have no file-based dedup, and Smart confirm commits a
/// trusted dupe without a review pause), and phantom purchases skew every cadence the predictor
/// learns. What comes back out:
///
///  - Its purchases (traced by <see cref="PurchaseEvent.ReceiptId"/> — never by matching values,
///    which is why a pre-provenance receipt is refused rather than guessed at).
///  - Products the receipt INTRODUCED (<see cref="Product.CreatedByReceiptId"/>) — but only while
///    they have no other history: a purchase from any other source or an inventory signal means the
///    household has invested in the product, and it stays (with the breadcrumb cleared).
///  - The merchant aliases it taught (<see cref="ProductAlias.TaughtByReceiptId"/>) — a later
///    confirm that re-pointed one became its new teacher, so that newer lesson is kept.
///  - The receipt row, its lines, and its saved image.
///
/// Deliberately NOT undone: re-tracking (visible state the user may have since endorsed, one tap to
/// flip back) and tags added to pre-existing products (no provenance; at worst a cosmetic extra tag).
/// </summary>
public sealed class ReceiptRemovalService(
    IHouseholdDbFactory dbFactory,
    ReceiptStorage storage,
    ILogger<ReceiptRemovalService> logger)
{
    /// <param name="Untraceable">The receipt was confirmed before purchase provenance existed, so
    /// "everything it did" cannot be safely identified — nothing was changed.</param>
    public sealed record Outcome(
        bool Found, bool Untraceable = false, int Purchases = 0, int ProductsRemoved = 0,
        int ProductsKept = 0, int AliasesRemoved = 0);

    public async Task<Outcome> RemoveAsync(int receiptId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Tracked on purpose: the whole removal is change-tracked entities in ONE SaveChanges.
        var receipt = await db.Receipts.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);
        if (receipt is null) return new Outcome(Found: false);

        int purchases = 0, productsRemoved = 0, productsKept = 0, aliasesRemoved = 0;

        if (receipt.Status == ReceiptStatus.Confirmed)
        {
            var linked = await db.PurchaseEvents
                .Where(p => p.ReceiptId == receipt.Id).ToListAsync(cancellationToken);

            // A confirmed receipt with no traceable purchases predates provenance (or its products
            // were deleted since, taking the purchases with them). Guessing by value-matching could
            // delete purchases another receipt legitimately recorded — refuse instead.
            if (linked.Count == 0) return new Outcome(Found: true, Untraceable: true);

            purchases = linked.Count;
            db.PurchaseEvents.RemoveRange(linked);

            // Products this receipt introduced: gone only while nothing else ever touched them.
            // "Something else" = a purchase from any other receipt / chat / manual entry, or an
            // inventory signal. Deleting a product cascades its tags, substitutes, and aliases.
            var introduced = await db.Products
                .Where(p => p.CreatedByReceiptId == receipt.Id).ToListAsync(cancellationToken);
            var removedProductIds = new HashSet<int>();
            foreach (var product in introduced)
            {
                var hasOtherHistory =
                    await db.PurchaseEvents.AnyAsync(
                        p => p.ProductId == product.Id && p.ReceiptId != receipt.Id, cancellationToken) ||
                    await db.InventorySignals.AnyAsync(
                        s => s.ProductId == product.Id, cancellationToken);
                if (hasOtherHistory)
                {
                    product.CreatedByReceiptId = null; // the receipt is going away; don't point at a ghost
                    productsKept++;
                }
                else
                {
                    db.Products.Remove(product);
                    removedProductIds.Add(product.Id);
                    productsRemoved++;
                }
            }

            // Aliases this receipt's confirm TAUGHT — by provenance, never by matching values: a
            // duplicate re-walks the same (merchant, raw text) pairs without becoming their teacher,
            // and one re-taught by a later confirm carries that later receipt's stamp and stays.
            // Ones on products being removed fall to the cascade instead.
            var aliases = await db.ProductAliases
                .Where(a => a.TaughtByReceiptId == receipt.Id).ToListAsync(cancellationToken);
            foreach (var alias in aliases)
            {
                if (removedProductIds.Contains(alias.ProductId)) continue; // cascade owns it
                db.ProductAliases.Remove(alias);
                aliasesRemoved++;
            }
        }
        // Pending/discarded receipts recorded nothing — removing them is just the row + image.

        db.Receipts.Remove(receipt); // lines go with it
        await db.SaveChangesAsync(cancellationToken);

        // Files AFTER the commit: a crash between the two leaves an orphaned folder (harmless, and
        // "delete my data" still reaches it) rather than a receipt row whose image is gone.
        storage.DeleteFolder(receipt.ImagePath);

        logger.LogInformation(
            "Removed receipt {ReceiptId}: {Purchases} purchase(s), {ProductsRemoved} product(s) removed, " +
            "{ProductsKept} kept (had other history), {Aliases} alias(es) untaught.",
            receipt.Id, purchases, productsRemoved, productsKept, aliasesRemoved);
        return new Outcome(Found: true, Purchases: purchases, ProductsRemoved: productsRemoved,
            ProductsKept: productsKept, AliasesRemoved: aliasesRemoved);
    }
}
