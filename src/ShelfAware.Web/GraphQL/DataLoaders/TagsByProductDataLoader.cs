using GreenDonut;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.GraphQL.DataLoaders;

/// <summary>Batches <c>product.tags</c> across a product list into ONE query keyed by product id — the
/// N+1 fix. <c>EfPantryStore.GetProductsAsync</c> eager-loads purchases but NOT tags, so a naive nested
/// <c>tags</c> resolver would query per product; this collects the ids Hot Chocolate asks for in a tick
/// and loads them together. The load runs through <see cref="IHouseholdDbFactory"/>, so the household
/// query filter scopes it exactly like every other read — a key from another household simply matches no
/// row.</summary>
public sealed class TagsByProductDataLoader(
    IHouseholdDbFactory dbFactory, IBatchScheduler batchScheduler, DataLoaderOptions options)
    : BatchDataLoader<int, ProductTag[]>(batchScheduler, options)
{
    protected override async Task<IReadOnlyDictionary<int, ProductTag[]>> LoadBatchAsync(
        IReadOnlyList<int> keys, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tags = await db.ProductTags
            .AsNoTracking()
            .Where(t => keys.Contains(t.ProductId))
            .ToListAsync(cancellationToken);
        return tags.GroupBy(t => t.ProductId).ToDictionary(g => g.Key, g => g.ToArray());
    }
}
