using GreenDonut;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.GraphQL.DataLoaders;

/// <summary>Batches <c>product.substitutes</c> across a product list into ONE query keyed by product id
/// (the N+1 fix — the same rationale as <see cref="TagsByProductDataLoader"/>; substitutes are also not
/// eager-loaded). Household-filtered through <see cref="IHouseholdDbFactory"/>.</summary>
public sealed class SubstitutesByProductDataLoader(
    IHouseholdDbFactory dbFactory, IBatchScheduler batchScheduler, DataLoaderOptions options)
    : BatchDataLoader<int, ProductSubstitute[]>(batchScheduler, options)
{
    protected override async Task<IReadOnlyDictionary<int, ProductSubstitute[]>> LoadBatchAsync(
        IReadOnlyList<int> keys, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var subs = await db.ProductSubstitutes
            .AsNoTracking()
            .Where(s => keys.Contains(s.ProductId))
            .ToListAsync(cancellationToken);
        return subs.GroupBy(s => s.ProductId).ToDictionary(g => g.Key, g => g.ToArray());
    }
}
