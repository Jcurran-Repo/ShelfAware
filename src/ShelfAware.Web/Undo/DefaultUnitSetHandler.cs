using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a product's default-unit change: which product, and the
/// before/after unit label (null = none). Display metadata only.</summary>
public sealed record DefaultUnitSetPayload(int ProductId, string? OldUnit, string? NewUnit, string ProductName);

/// <summary>Undo for a default-unit change: restore the old unit. Refuses
/// <see cref="UndoResult.Superseded"/> if the unit was changed again since, and
/// <see cref="UndoResult.Gone"/> if the product is deleted.</summary>
public sealed class DefaultUnitSetHandler : UndoHandler<DefaultUnitSetPayload>
{
    public override ActivityKind Kind => ActivityKind.DefaultUnitSet;

    protected override string Summarize(DefaultUnitSetPayload p) =>
        string.IsNullOrEmpty(p.NewUnit) ? $"Cleared {p.ProductName}'s unit" : $"Set {p.ProductName}'s unit to {p.NewUnit}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, DefaultUnitSetPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(x => x.Id == p.ProductId, ct);
        if (product is null) return UndoResult.Gone;
        if (product.DefaultUnit != p.NewUnit) return UndoResult.Superseded; // changed again since
        product.DefaultUnit = p.OldUnit;
        return UndoResult.Done;
    }
}
