using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.GraphQL;

/// <summary>The read-only GraphQL root. Every resolver loads through <see cref="IHouseholdDbFactory"/>
/// (AsNoTracking), so the household query filter scopes every read for free — the SAME path the UI and
/// chat use. There is deliberately no <c>Mutation</c> type: the API is read-only by absence, not by a
/// guard, so an external client can never write into a pantry.
///
/// ⚠️ Resolvers must NEVER reach a pantry context any other way (a raw <c>IDbContextFactory</c> or
/// <c>IgnoreQueryFilters</c>) — that would bypass the stamping that makes a token for household A unable
/// to express a query for B's data. Computed fields (prediction/estimate) and the tags/substitutes
/// DataLoaders arrive in phase 4; they run the pure Core engine over already-scoped rows.</summary>
public sealed class Query
{
    /// <summary>Every tracked product in the caller's household, name-ordered (mirrors
    /// <c>EfPantryStore.GetProductsAsync</c>). Purchases are eager-loaded so nested <c>purchases</c>
    /// resolves without an N+1; tags/substitutes are DataLoader'd in phase 4.</summary>
    public async Task<IReadOnlyList<Product>> GetProducts(IHouseholdDbFactory dbFactory, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Products
            .AsNoTracking()
            .Include(p => p.Purchases)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    /// <summary>One product by id, or null. The household filter scopes the lookup, so a foreign
    /// household's id comes back null exactly like an id that never existed — no cross-household read,
    /// no existence oracle (mirrors <c>EfPantryStore.GetProductAsync</c>).</summary>
    public async Task<Product?> GetProduct(int id, IHouseholdDbFactory dbFactory, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Products
            .AsNoTracking()
            .Include(p => p.Purchases)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <summary>The caller's saved recipes, name-ordered. Ingredients, steps, and tags are eager-loaded
    /// (small per-recipe) so the nested fields resolve without an N+1.</summary>
    public async Task<IReadOnlyList<Recipe>> GetRecipes(IHouseholdDbFactory dbFactory, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Recipes
            .AsNoTracking()
            .Include(r => r.Ingredients)
            .Include(r => r.Steps)
            .Include(r => r.Tags)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
    }
}
