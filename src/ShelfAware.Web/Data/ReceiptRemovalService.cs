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
///    they have no other history: a purchase from any other source, an inventory signal, or an
///    attested count means the household has invested in the product, and it stays (with the
///    breadcrumb cleared).
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
        var (outcome, imagePathToDelete) = await RemoveOnAsync(db, receiptId, cancellationToken);
        // Nothing staged (receipt not found, or a pre-provenance receipt that can't be safely reversed):
        // no save, no folder. Read from the OUTCOME, not the image path — a receipt could in principle
        // carry no image and still have been staged for removal.
        if (!outcome.Found || outcome.Untraceable) return outcome;

        await db.SaveChangesAsync(cancellationToken);

        // Files AFTER the commit: a crash between the two leaves an orphaned folder (harmless, and
        // "delete my data" still reaches it) rather than a receipt row whose image is gone.
        if (imagePathToDelete is not null) storage.DeleteFolder(imagePathToDelete);

        logger.LogInformation(
            "Removed receipt {ReceiptId}: {Purchases} purchase(s), {ProductsRemoved} product(s) removed, " +
            "{ProductsKept} kept (had other history), {Aliases} alias(es) untaught.",
            receiptId, outcome.Purchases, outcome.ProductsRemoved, outcome.ProductsKept, outcome.AliasesRemoved);
        return outcome;
    }

    /// <summary>The DB half of a removal, STAGED on the caller's context — no SaveChanges, and no file
    /// deletion. Extracted so the receipt-confirm UNDO can reuse the exact same reversal: its handler
    /// stages this, <c>ActivityLogService.PeekAsync</c> discards it (⚠️ which is why the image-folder
    /// delete is NOT here — a Peek re-runs the reversal to test undoability and a Peek must never touch the
    /// filesystem), and <c>UndoAsync</c> commits it and then deletes the image via the post-commit hook.
    /// Returns the outcome and the image folder to delete AFTER a successful commit — <c>null</c> when
    /// nothing was staged (receipt not found, or a pre-provenance receipt that can't be safely reversed),
    /// so the caller knows there is neither a save nor a folder to make.</summary>
    public static async Task<(Outcome Outcome, string? ImagePathToDelete)> RemoveOnAsync(
        ShelfAwareDbContext db, int receiptId, CancellationToken cancellationToken = default)
    {
        // Tracked on purpose: the whole removal is change-tracked entities in ONE SaveChanges.
        var receipt = await db.Receipts.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);
        if (receipt is null) return (new Outcome(Found: false), null);

        int purchases = 0, productsRemoved = 0, productsKept = 0, aliasesRemoved = 0;

        if (receipt.Status == ReceiptStatus.Confirmed)
        {
            var linked = await db.PurchaseEvents
                .Include(p => p.Product) // §13.2's undo needs each purchase's product to move the count
                .Where(p => p.ReceiptId == receipt.Id).ToListAsync(cancellationToken);

            // A confirmed receipt with no traceable purchases predates provenance (or its products
            // were deleted since, taking the purchases with them). Guessing by value-matching could
            // delete purchases another receipt legitimately recorded — refuse instead (nothing staged).
            if (linked.Count == 0) return (new Outcome(Found: true, Untraceable: true), null);

            purchases = linked.Count;

            // §13.2, the other half: take back exactly what the confirm put in, BEFORE the purchases
            // go. Same StockLedger the confirm used, so the two can't drift into disagreeing about how
            // much a removal owes. Products deleted below don't care — their row goes with them.
            //
            // ⚠️ …except past a LOOK the human took after the confirm. An absolute count attested
            // after ConfirmedAt already reflects whatever this receipt did or didn't put on the shelf
            // — recount 6 after a duplicate's phantom +3 and the 6 is the truth, phantom excluded —
            // so subtracting would overrule newer, better evidence. This comparison is only sound
            // because ONLY an absolute count advances QuantityCountedAt: a relative "Used one"
            // carries phantom stock forward rather than re-baselining, so it must not (and does not)
            // shield the count from the subtract. Null ConfirmedAt is a pre-v4.1 confirm with no
            // moment to compare against — subtract as always, erring toward an early rebuy —
            // ⚠️ EXCEPT for a product this receipt INTRODUCED: it did not exist before its own
            // confirm, so every attestation on it provably postdates the confirm and the null
            // timestamp still has a decidable order. Without this, keeping the product (below) while
            // subtracting here silently corrupted the very count the keep exists to preserve.
            foreach (var purchase in linked)
            {
                if (purchase.Product is not { } product) continue;
                if (product.QuantityCountedAt is { } counted
                    && (receipt.ConfirmedAt is { } confirmedAt
                        ? counted > confirmedAt
                        : product.CreatedByReceiptId == receipt.Id)) continue;
                StockLedger.Remove(product, purchase.Quantity);
            }

            db.PurchaseEvents.RemoveRange(linked);

            // Products this receipt introduced: gone only while nothing else ever touched them.
            // "Something else" = a purchase from any other receipt / chat / manual entry, an
            // inventory signal, or an ATTESTED COUNT — a census or the count panel is a human at
            // the shelf saying "I have these", the same investment a signal proves, and it counts
            // even dormant (stop-counting keeps the number as history, §13.1). A census writes no
            // purchase and, for a positive count, no signal, so without this clause the census's
            // own population — a receipt-introduced product carrying a fresh count — died with its
            // receipt. The subtract guard above already treats a post-confirm attestation as the
            // better evidence about the count; the two halves must not disagree about whether an
            // attestation is history. Deleting a product cascades its tags, substitutes, and aliases.
            var introduced = await db.Products
                .Where(p => p.CreatedByReceiptId == receipt.Id).ToListAsync(cancellationToken);
            var removedProductIds = new HashSet<int>();
            foreach (var product in introduced)
            {
                var hasOtherHistory =
                    product.QuantityCountedAt is not null ||
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
        // Staged, not saved: the caller commits, then deletes the image folder this returns.
        return (new Outcome(Found: true, Purchases: purchases, ProductsRemoved: productsRemoved,
            ProductsKept: productsKept, AliasesRemoved: aliasesRemoved), receipt.ImagePath);
    }
}
