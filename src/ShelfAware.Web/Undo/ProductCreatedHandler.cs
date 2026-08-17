using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a created product: which product, and its name for the summary.</summary>
public sealed record ProductCreatedPayload(int ProductId, string ProductName);

/// <summary>Undo for a created product: delete it — but ONLY if it has gained no other history since
/// (no purchase, no signal, no count). A product the household then invested in is theirs to keep, so
/// this reports <see cref="UndoResult.Superseded"/> instead, and the page greys the entry. Mirrors
/// <c>ReceiptRemovalService</c>'s "keep the product that earned its keep." Deleting cascades the tags and
/// substitutes created with it. <see cref="UndoResult.Gone"/> if it's already deleted.</summary>
public sealed class ProductCreatedHandler : UndoHandler<ProductCreatedPayload>
{
    public override ActivityKind Kind => ActivityKind.ProductCreated;

    protected override string Summarize(ProductCreatedPayload p) => $"Added {p.ProductName}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, ProductCreatedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var product = await db.Products
            .Include(x => x.Purchases)
            .Include(x => x.Signals)
            .FirstOrDefaultAsync(x => x.Id == p.ProductId, ct);
        if (product is null) return UndoResult.Gone;
        if (product.Purchases.Count > 0 || product.Signals.Count > 0 || product.QuantityCountedAt is not null)
            return UndoResult.Superseded; // it gained real history — keep it

        db.Products.Remove(product); // cascades the tags/substitutes created with it
        return UndoResult.Done;
    }
}
