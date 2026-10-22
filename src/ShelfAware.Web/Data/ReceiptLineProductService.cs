using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Ingest;
using ShelfAware.Core.Tagging;
using ShelfAware.Web.Undo;

namespace ShelfAware.Web.Data;

/// <summary>
/// Tracks a CONFIRMED receipt line that points at no product — one the reviewer ✕'d out of the grid,
/// or one whose product was deleted since (deletion detaches the lines rather than erasing the
/// receipt's audit trail). The /receipts callout offers it; this is the write behind it.
/// <para>The line is linked to a product and its purchase is recorded on the receipt's date, through the
/// confirm's own <see cref="ReceiptConfirmationService.RecordPurchase"/> — so the product's history says
/// what the receipt beside it says, and removing the receipt later unwinds this purchase (and a product
/// it introduced) exactly as it would one the confirm recorded. It is a human pairing, so it teaches the
/// merchant alias a reviewed confirm would have, and it is logged and undoable from /history.</para>
/// </summary>
public class ReceiptLineProductService(IHouseholdDbFactory dbFactory, IActivityLog activityLog)
{
    /// <summary>What tracking a line would do, decided by product identity alone.</summary>
    public enum PlanKind
    {
        /// <summary>No product has this name — tracking creates one.</summary>
        Create,
        /// <summary>Exactly one product already has this name — tracking links to it, never a duplicate.</summary>
        Link,
        /// <summary>Two or more products share this name — which one is the household's call (merge them first).</summary>
        Ambiguous,
        /// <summary>The name has no letters or digits, so no product could ever match it.</summary>
        Unnamable,
    }

    /// <param name="Existing">The product a <see cref="PlanKind.Link"/> plan links to; null otherwise.</param>
    public sealed record Plan(PlanKind Kind, Product? Existing);

    /// <summary>THE answer to "what would tracking this line do" — the /receipts button's label and the
    /// write below both ask it, so the button can't promise a new product the write then dedupes away.
    /// Identity is <see cref="ProductMatcher.ExactMatches"/>, never a raw string comparison: a fuzzy
    /// similarity is a guess, and a guess must not decide which product's history a purchase joins.</summary>
    public static Plan PlanFor(string name, IReadOnlyList<Product> products)
    {
        if (ProductMatcher.IdentityKey(name).Length == 0) return new(PlanKind.Unnamable, null);
        var matches = ProductMatcher.ExactMatches(name, products);
        return matches.Count switch
        {
            0 => new(PlanKind.Create, null),
            1 => new(PlanKind.Link, matches[0]),
            _ => new(PlanKind.Ambiguous, null),
        };
    }

    /// <summary>Whether a receipt's purchases can be told apart from everyone else's. A confirm from
    /// before purchases carried their <see cref="PurchaseEvent.ReceiptId"/> left linked lines whose
    /// purchases name no receipt: stamping ONE purchase onto such a receipt would make it look removable,
    /// and removing it would then leave every unstamped purchase behind. A receipt with no linked lines
    /// left has nothing unstamped to strand, whatever its age.</summary>
    /// <param name="hasStampedPurchase">Any purchase carries this receipt's id.</param>
    public static bool HasProvenance(Receipt receipt, bool hasStampedPurchase) =>
        hasStampedPurchase || receipt.Lines.All(l => l.ProductId is null);

    /// <summary>THE answer to "does this line get the track offer" — the /receipts chip, callout and
    /// per-line button all ask it, and <see cref="TrackAsync"/> refuses exactly what it excludes. A
    /// pending receipt's lines are settled by review; a name that can't be a product is never offered.
    /// <paramref name="receipt"/> must have its lines loaded.</summary>
    public static bool IsTrackable(Receipt receipt, ReceiptLine line, bool hasStampedPurchase) =>
        receipt.Status == ReceiptStatus.Confirmed
        && receipt.PurchasedAt is not null
        && line.ProductId is null
        && ProductMatcher.IdentityKey(line.NormalizedName).Length > 0
        && HasProvenance(receipt, hasStampedPurchase);

    public enum Status
    {
        /// <summary>A new product was created from the line and linked.</summary>
        Created,
        /// <summary>The line was linked to the one existing product with its name.</summary>
        Linked,
        /// <summary>No such line in this household.</summary>
        NotFound,
        /// <summary>The receipt is still pending (or undated) — its lines are settled by review, not here.</summary>
        NotConfirmed,
        /// <summary>The line already points at a product (another tab got there first).</summary>
        AlreadyLinked,
        /// <summary>Products share the line's name; nothing was written.</summary>
        Ambiguous,
        /// <summary>The line's name can't name a product; nothing was written.</summary>
        Unnamable,
        /// <summary>The receipt predates purchase provenance (<see cref="HasProvenance"/>); nothing was written.</summary>
        Untraceable,
    }

    /// <param name="ProductId">The product the line now points at — set for Created, Linked and AlreadyLinked.</param>
    /// <param name="ProductName">That product's name, for the page's confirmation line.</param>
    /// <param name="Retracked">Whether the purchase turned an untracked product back on.</param>
    public sealed record Outcome(Status Status, int? ProductId = null, string? ProductName = null, bool Retracked = false);

    /// <summary>
    /// Link the line to the product its plan names (creating it if none), record its purchase on the
    /// receipt's date, apply its reviewed tags, teach its merchant alias, and log the undo — one
    /// transaction, so a failure writes nothing. The plan is re-taken here against the household's
    /// CURRENT catalog, not the one the page rendered: a product created since then is linked to rather
    /// than duplicated, and the outcome says which happened.
    /// </summary>
    public async Task<Outcome> TrackAsync(int lineId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // The household query filter scopes this lookup — another household's line id comes back null
        // exactly like one that never existed, so a tampered id can't link someone else's receipt.
        var line = await db.ReceiptLines.Include(l => l.Receipt).ThenInclude(r => r!.Lines)
            .FirstOrDefaultAsync(l => l.Id == lineId, cancellationToken);
        if (line?.Receipt is not { } receipt) return new(Status.NotFound);
        if (receipt.Status != ReceiptStatus.Confirmed || receipt.PurchasedAt is not { } purchasedAt)
            return new(Status.NotConfirmed);
        if (line.ProductId is { } linkedId) return await AlreadyLinked(db, linkedId, cancellationToken);
        var stamped = await db.PurchaseEvents.AnyAsync(p => p.ReceiptId == receipt.Id, cancellationToken);
        if (!HasProvenance(receipt, stamped)) return new(Status.Untraceable);

        var name = line.NormalizedName.Trim();
        var products = await db.Products.Include(p => p.Tags).ToListAsync(cancellationToken);
        var plan = PlanFor(name, products);
        if (plan.Kind == PlanKind.Unnamable) return new(Status.Unnamable);
        if (plan.Kind == PlanKind.Ambiguous) return new(Status.Ambiguous);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        Product product;
        IReadOnlyCollection<string?> priorSizes = [];
        if (plan.Existing is { } existing)
        {
            product = existing;
            priorSizes = await db.PurchaseEvents.Where(p => p.ProductId == product.Id)
                .Select(p => p.Size).ToListAsync(cancellationToken);
        }
        else
        {
            // CreatedByReceiptId: this receipt introduced the product, so removing the receipt takes
            // it too while nothing else has touched it — the confirm's provenance rule. The stored
            // category came off a circuit <select> at review, so it gets the same IsDefined guard.
            product = new Product
            {
                Name = name,
                Category = Enum.IsDefined(line.Category) ? line.Category : Category.Other,
                CreatedByReceiptId = receipt.Id,
            };
            db.Products.Add(product);
        }

        // A count attested since the confirm already holds this purchase (or its consumption) — adding
        // into it would be stock that was never bought twice, and the receipt's removal would refuse to
        // take it back out. Same reading the removal makes, so the add and its reversal stay symmetric.
        var moveCount = !ReceiptRemovalService.CountPostdatesConfirm(product, receipt);
        var onHandBefore = product.QuantityOnHand;
        var recorded = ReceiptConfirmationService.RecordPurchase(db, product, receipt.Id, purchasedAt,
            line.Quantity, line.Brand, line.Size, line.Variety, line.ExpirationDate, moveCount);
        var purchase = recorded.Purchase;
        // What the ledger actually moved — nothing for an uncounted product — is what the undo gives back.
        var countMoved = (product.QuantityOnHand ?? 0m) - (onHandBefore ?? 0m);

        // The line as the confirm would have left it: cleaned like its purchase, and stamped with the
        // same soft pack-misread check — a ✕'d line never went through either.
        line.Brand = purchase.Brand;
        line.Size = purchase.Size;
        line.Variety = purchase.Variety;
        line.Quantity = purchase.Quantity;
        line.QuantityFlag = QuantityAnomaly.Check(purchase.Quantity, purchase.Size, priorSizes);

        var tags = ReceiptConfirmationService.DeserializeTags(line.TagsJson);
        if (tags.Count > 0)
            TagVocabulary.ApplyTags(product, tags, await EfPantryStore.LoadVocabularyAsync(db, cancellationToken));

        // A human just paired this raw text with this product — the trust a reviewed confirm's alias
        // rests on — so teach it the same way, stamped with its teacher so removal untaught it. An alias
        // the merchant already has for this text stays as it is: it was taught by a receipt of its own.
        var merchant = receipt.Merchant ?? "";
        ProductAlias? alias = null;
        if (!await db.ProductAliases.AnyAsync(a => a.Merchant == merchant && a.RawText == line.RawText, cancellationToken))
        {
            alias = new ProductAlias
            {
                Merchant = merchant, RawText = line.RawText, Product = product, TaughtByReceiptId = receipt.Id,
            };
            db.ProductAliases.Add(alias);
        }
        await db.SaveChangesAsync(cancellationToken); // assigns the product, purchase and alias ids

        // The claim: the link lands only while the line still points at nothing. Two taps racing past
        // the read above both get here, but SQLite runs one write at a time, so the second finds the
        // line taken and its whole transaction — product, purchase, alias — rolls back.
        var claimed = await db.ReceiptLines
            .Where(l => l.Id == line.Id && l.ProductId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ProductId, product.Id), cancellationToken);
        if (claimed == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            var winner = await db.ReceiptLines.AsNoTracking().Where(l => l.Id == line.Id)
                .Select(l => l.ProductId).FirstOrDefaultAsync(cancellationToken);
            return winner is { } winnerId ? await AlreadyLinked(db, winnerId, cancellationToken) : new(Status.NotFound);
        }

        activityLog.Record(db, ActivityKind.ReceiptLineTracked, new ReceiptLineTrackedPayload(
            line.Id, purchase.Id, product.Id, product.Name, CreatedProduct: plan.Kind == PlanKind.Create,
            purchase.Quantity, countMoved, alias?.Id));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken); // retention: best-effort, after the commit
        return new(plan.Kind == PlanKind.Create ? Status.Created : Status.Linked, product.Id, product.Name,
            recorded.Retracked);
    }

    private static async Task<Outcome> AlreadyLinked(ShelfAwareDbContext db, int productId, CancellationToken ct) =>
        new(Status.AlreadyLinked, productId,
            await db.Products.Where(p => p.Id == productId).Select(p => p.Name).FirstOrDefaultAsync(ct));
}
