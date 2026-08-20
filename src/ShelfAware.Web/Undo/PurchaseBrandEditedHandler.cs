using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a purchase-brand correction: which purchase, and the before/after
/// brand (null = unbranded). Cosmetic per-purchase metadata — no count moves.</summary>
public sealed record PurchaseBrandEditedPayload(
    int PurchaseId, string? OldBrand, string? NewBrand, string ProductName);

/// <summary>Undo for a purchase-brand correction: restore the old brand. Refuses
/// <see cref="UndoResult.Superseded"/> if the brand was changed again since (current no longer matches
/// what this edit set), and <see cref="UndoResult.Gone"/> if the purchase is deleted.</summary>
public sealed class PurchaseBrandEditedHandler : UndoHandler<PurchaseBrandEditedPayload>
{
    public override ActivityKind Kind => ActivityKind.PurchaseBrandEdited;

    protected override string Summarize(PurchaseBrandEditedPayload p) =>
        $"Set {p.ProductName}'s brand to {Show(p.NewBrand)}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, PurchaseBrandEditedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var purchase = await db.PurchaseEvents.FirstOrDefaultAsync(x => x.Id == p.PurchaseId, ct);
        if (purchase is null) return UndoResult.Gone;
        if (purchase.Brand != p.NewBrand) return UndoResult.Superseded; // changed again since
        purchase.Brand = p.OldBrand;
        return UndoResult.Done;
    }

    private static string Show(string? brand) => string.IsNullOrEmpty(brand) ? "unbranded" : brand;
}
