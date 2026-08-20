using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a product rename: which product, and the before/after names.</summary>
public sealed record ProductRenamedPayload(int ProductId, string OldName, string NewName);

/// <summary>Undo for a product rename: rename it back through the SAME <c>ProductRenameService.RenameOnAsync</c>
/// the forward rename used (so the re-pointing of recipe links and the can't-merge collision check are one
/// definition, run in reverse). Refuses <see cref="UndoResult.Superseded"/> if it was renamed again since
/// (current name no longer matches what this set) or if the old name is now taken by another product (the
/// rename-back would collide), and <see cref="UndoResult.Gone"/> if the product is deleted.</summary>
public sealed class ProductRenamedHandler : UndoHandler<ProductRenamedPayload>
{
    public override ActivityKind Kind => ActivityKind.ProductRenamed;

    protected override string Summarize(ProductRenamedPayload p) => $"Renamed {p.OldName} to {p.NewName}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, ProductRenamedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var product = await db.Products.FindAsync([p.ProductId], ct);
        if (product is null) return UndoResult.Gone;
        if (!string.Equals(product.Name, p.NewName, StringComparison.Ordinal))
            return UndoResult.Superseded; // renamed again since — undoing would clobber that

        var outcome = await ProductRenameService.RenameOnAsync(db, p.ProductId, p.OldName, ct);
        return outcome.Ok ? UndoResult.Done : UndoResult.Superseded; // e.g. the old name is taken now
    }
}
