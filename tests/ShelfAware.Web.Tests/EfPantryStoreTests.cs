using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

public class EfPantryStoreTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EfPantryStore _store;

    public EfPantryStoreTests() => _store = new EfPantryStore(_db);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Adding_a_purchase_retracks_an_ignored_product_and_reports_it()
    {
        // The chat/voice "bought X" path must end "don't want it for a while" (grocery-list Untrack)
        // the same way a receipt confirm does, and return true so the assistant can say so.
        int cocoaId;
        await using (var db = _db.CreateDbContext())
        {
            var cocoa = new Product { Name = "Cocoa Powder", IsTracked = false };
            db.Products.Add(cocoa);
            await db.SaveChangesAsync();
            cocoaId = cocoa.Id;
        }

        var retracked = await _store.AddPurchaseAsync(cocoaId, new DateOnly(2026, 7, 6), 1m);

        Assert.True(retracked);
        await using var read = _db.CreateDbContext();
        Assert.True((await read.Products.SingleAsync(p => p.Id == cocoaId)).IsTracked);
        Assert.Equal(1, await read.PurchaseEvents.CountAsync(pe => pe.ProductId == cocoaId));
    }

    [Fact]
    public async Task Adding_a_purchase_to_a_tracked_product_reports_no_retrack()
    {
        int milkId;
        await using (var db = _db.CreateDbContext())
        {
            var milk = new Product { Name = "Whole Milk" }; // IsTracked defaults to true
            db.Products.Add(milk);
            await db.SaveChangesAsync();
            milkId = milk.Id;
        }

        var retracked = await _store.AddPurchaseAsync(milkId, new DateOnly(2026, 7, 6), 1m);

        Assert.False(retracked);
        await using var read = _db.CreateDbContext();
        Assert.Equal(1, await read.PurchaseEvents.CountAsync(pe => pe.ProductId == milkId));
    }
}
