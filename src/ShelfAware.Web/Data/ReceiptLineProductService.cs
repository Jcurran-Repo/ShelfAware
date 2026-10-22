using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Tagging;

namespace ShelfAware.Web.Data;

/// <summary>
/// Tracks a CONFIRMED receipt line that points at no product — one the reviewer ✕'d out of the grid,
/// or one whose product was deleted since (deletion detaches the lines rather than erasing the
/// receipt's audit trail). The /receipts callout offers it; this is the write behind it.
/// <para>The line is linked to a product and its purchase is recorded on the receipt's date, through the
/// confirm's own <see cref="ReceiptConfirmationService.RecordPurchase"/> — so the product's history says
/// what the receipt beside it says, and removing the receipt later unwinds this purchase (and a product
/// it introduced) exactly as it would one the confirm recorded.</para>
/// </summary>
public class ReceiptLineProductService(IHouseholdDbFactory dbFactory)
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

    public enum Status
    {
        /// <summary>A new product was created from the line and linked.</summary>
        Created,
        /// <summary>The line was linked to the one existing product with its name.</summary>
        Linked,
        /// <summary>No such line in this household.</summary>
        NotFound,
        /// <summary>The receipt is still pending — its lines are settled by review, not here.</summary>
        NotConfirmed,
        /// <summary>The line already points at a product (another tab got there first).</summary>
        AlreadyLinked,
        /// <summary>Products share the line's name; nothing was written.</summary>
        Ambiguous,
        /// <summary>The line's name can't name a product; nothing was written.</summary>
        Unnamable,
    }

    /// <param name="ProductId">The product the line now points at — set for Created, Linked and AlreadyLinked.</param>
    /// <param name="ProductName">That product's name, for the page's confirmation line.</param>
    /// <param name="Retracked">Whether the purchase turned an untracked product back on.</param>
    public sealed record Outcome(Status Status, int? ProductId = null, string? ProductName = null, bool Retracked = false);

    /// <summary>
    /// Link the line to the product its plan names (creating it if none), record its purchase on the
    /// receipt's date, and apply its reviewed tags — one SaveChanges, so a failure writes nothing. The
    /// plan is re-taken here against the household's CURRENT catalog, not the one the page rendered: a
    /// product created since then is linked to rather than duplicated, and the outcome says which happened.
    /// </summary>
    public async Task<Outcome> TrackAsync(int lineId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // The household query filter scopes this lookup — another household's line id comes back null
        // exactly like one that never existed, so a tampered id can't link someone else's receipt.
        var line = await db.ReceiptLines.Include(l => l.Receipt)
            .FirstOrDefaultAsync(l => l.Id == lineId, cancellationToken);
        if (line?.Receipt is not { } receipt) return new(Status.NotFound);
        if (receipt.Status != ReceiptStatus.Confirmed || receipt.PurchasedAt is not { } purchasedAt)
            return new(Status.NotConfirmed);
        if (line.ProductId is { } linkedId)
            return new(Status.AlreadyLinked, linkedId,
                await db.Products.Where(p => p.Id == linkedId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken));

        var name = line.NormalizedName.Trim();
        var products = await db.Products.Include(p => p.Tags).ToListAsync(cancellationToken);
        var plan = PlanFor(name, products);

        Product product;
        switch (plan.Kind)
        {
            case PlanKind.Unnamable: return new(Status.Unnamable);
            case PlanKind.Ambiguous: return new(Status.Ambiguous);
            case PlanKind.Link:
                product = plan.Existing!;
                break;
            default:
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
                break;
        }

        var retracked = ReceiptConfirmationService.RecordPurchase(db, product, receipt.Id, purchasedAt,
            line.Quantity > 0 ? line.Quantity : 1m, line.Brand, line.Size, line.Variety, line.ExpirationDate);
        var tags = ReceiptConfirmationService.DeserializeTags(line.TagsJson);
        if (tags.Count > 0)
            TagVocabulary.ApplyTags(product, tags, await EfPantryStore.LoadVocabularyAsync(db, cancellationToken));
        line.Product = product;

        await db.SaveChangesAsync(cancellationToken);
        return new(plan.Kind == PlanKind.Create ? Status.Created : Status.Linked, product.Id, product.Name, retracked);
    }
}
