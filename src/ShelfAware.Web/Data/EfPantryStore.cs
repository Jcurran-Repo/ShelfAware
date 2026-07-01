using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Data;

/// <summary>EF Core implementation of the chat data port (DESIGN.md §3/§7).</summary>
public class EfPantryStore(IDbContextFactory<ShelfAwareDbContext> dbFactory) : IPantryStore
{
    public async Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Products
            .Include(p => p.Purchases)
            .Include(p => p.Signals)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CreateProductAsync(string name, Category category, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = new Product { Name = name, Category = category };
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        return product.Id;
    }

    public async Task AddPurchaseAsync(int productId, DateOnly purchasedAt, decimal quantity, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.PurchaseEvents.Add(new PurchaseEvent
        {
            ProductId = productId,
            PurchasedAt = purchasedAt,
            Quantity = quantity,
            Source = PurchaseSource.Chat,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordSignalAsync(int productId, SignalKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.InventorySignals.Add(new InventorySignal
        {
            ProductId = productId,
            Kind = kind,
            SignaledAt = DateTimeOffset.Now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetTrackingAsync(int productId, bool tracked, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return;
        product.IsTracked = tracked;
        await db.SaveChangesAsync(cancellationToken);
    }
}
