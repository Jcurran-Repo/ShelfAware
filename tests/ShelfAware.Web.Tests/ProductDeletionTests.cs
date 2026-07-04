using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Regression tests for the product-delete FK crash: ReceiptLine.ProductId is an OPTIONAL FK with no
/// DB-level ON DELETE action, and the lines aren't loaded when the Products page deletes — so the raw
/// delete blows up on any product that appeared on a confirmed receipt. The first test pins the
/// failure mode (proving the constraint is real); the second pins the fixed sequence Products.razor
/// now uses (detach lines, then delete inside one transaction).
/// </summary>
public class ProductDeletionTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedProductWithConfirmedReceiptLine()
    {
        await using var db = _db.CreateDbContext();
        var product = new Product
        {
            Name = "Whole Milk",
            Tags = [new ProductTag { Value = "Dairy Staple" }],
            Purchases = [new PurchaseEvent { PurchasedAt = new DateOnly(2026, 6, 1) }],
            Signals = [new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = DateTimeOffset.Now }],
        };
        var receipt = new Receipt
        {
            Merchant = "Walmart",
            ImagePath = "receipts/test",
            Status = ReceiptStatus.Confirmed,
            Lines = [new ReceiptLine { RawText = "GV WHL MLK", NormalizedName = "Whole Milk", Product = product }],
        };
        db.Products.Add(product);
        db.Receipts.Add(receipt);
        db.ProductAliases.Add(new ProductAlias { Merchant = "Walmart", RawText = "GV WHL MLK", Product = product });
        await db.SaveChangesAsync();
        return product.Id;
    }

    [Fact]
    public async Task Naive_delete_of_a_receipt_sourced_product_is_blocked_by_the_fk()
    {
        var id = await SeedProductWithConfirmedReceiptLine();

        await using var db = _db.CreateDbContext();
        var product = await db.Products
            .Include(p => p.Purchases).Include(p => p.Signals).Include(p => p.Tags)
            .SingleAsync(p => p.Id == id);
        db.Products.Remove(product);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Fixed_delete_detaches_lines_then_cascades_everything_else()
    {
        var id = await SeedProductWithConfirmedReceiptLine();

        await using (var db = _db.CreateDbContext())
        {
            var product = await db.Products
                .Include(p => p.Purchases).Include(p => p.Signals).Include(p => p.Tags)
                .SingleAsync(p => p.Id == id);
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.ReceiptLines.Where(l => l.ProductId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ProductId, (int?)null));
            db.Products.Remove(product);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await using var check = _db.CreateDbContext();
        Assert.Empty(await check.Products.ToListAsync());
        Assert.Empty(await check.PurchaseEvents.ToListAsync());
        Assert.Empty(await check.InventorySignals.ToListAsync());
        Assert.Empty(await check.ProductTags.ToListAsync());
        Assert.Empty(await check.ProductAliases.ToListAsync());
        // The receipt's audit trail survives — the line just points at no product now.
        var line = await check.ReceiptLines.SingleAsync();
        Assert.Null(line.ProductId);
    }
}
