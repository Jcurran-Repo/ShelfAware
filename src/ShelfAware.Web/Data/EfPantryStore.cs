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
            .AsNoTracking() // read-only: the chat resolves/reads these; mutations use their own contexts
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

    public async Task<IReadOnlyList<RecipeRef>> GetRecipesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Recipes
            .OrderBy(r => r.Name)
            .Select(r => new RecipeRef(r.Id, r.Name, r.Steps.Count > 0))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> AddSubstitutesAsync(int productId, IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ProductSubstitutes
            .Where(s => s.ProductId == productId)
            .Select(s => s.Value)
            .ToListAsync(cancellationToken);
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        foreach (var value in values)
        {
            var v = value.Trim();
            if (v.Length == 0 || !have.Add(v)) continue;
            db.ProductSubstitutes.Add(new ProductSubstitute { ProductId = productId, Value = v });
            added.Add(v);
        }
        if (added.Count > 0) await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    public async Task<IReadOnlyList<string>> GetExcludedFoodsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ExcludedFoods.Select(f => f.Value).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> AddGroceryExtrasAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var have = new HashSet<string>(
            await db.GroceryExtras.Select(e => e.Name).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        foreach (var name in names)
        {
            var n = name.Trim();
            if (n.Length == 0 || !have.Add(n)) continue;
            db.GroceryExtras.Add(new GroceryExtra { Name = n });
            added.Add(n);
        }
        if (added.Count > 0) await db.SaveChangesAsync(cancellationToken);
        return added;
    }
}
