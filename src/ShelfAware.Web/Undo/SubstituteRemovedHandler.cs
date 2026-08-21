using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>The product and the "also works as" phrase that was removed. The undo re-adds the phrase to that
/// product; the product name is for the summary.</summary>
public sealed record SubstituteRemovedPayload(int ProductId, string ProductName, string Value);

/// <summary>Undo for removing a product's "also works as" substitute: put it back, unless the product is
/// gone (<see cref="UndoResult.Gone"/>) or the phrase is already back (re-added by hand →
/// <see cref="UndoResult.Gone"/>). The symmetric counterpart to <c>SubstitutesAddedHandler</c>.</summary>
public sealed class SubstituteRemovedHandler : UndoHandler<SubstituteRemovedPayload>
{
    public override ActivityKind Kind => ActivityKind.SubstituteRemoved;

    protected override string Summarize(SubstituteRemovedPayload p) =>
        $"Removed \"{p.Value}\" from {p.ProductName}'s substitutes";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, SubstituteRemovedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        if (await db.Products.FindAsync([p.ProductId], ct) is null) return UndoResult.Gone; // product deleted since
        var alreadyBack = (await db.ProductSubstitutes.Where(s => s.ProductId == p.ProductId).ToListAsync(ct))
            .Any(s => string.Equals(s.Value, p.Value, StringComparison.OrdinalIgnoreCase));
        if (alreadyBack) return UndoResult.Gone;
        db.ProductSubstitutes.Add(new ProductSubstitute { ProductId = p.ProductId, Value = p.Value });
        return UndoResult.Done;
    }
}
