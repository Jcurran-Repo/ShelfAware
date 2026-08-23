using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;
using ShelfAware.Web.GraphQL;

namespace ShelfAware.Web.Tests;

/// <summary>The read-only GraphQL API's tenancy + shape. The headline: every root resolver reads through
/// <see cref="IHouseholdDbFactory"/>, so a token scoped to household A can only ever see A's rows — the
/// query filter makes a query for B's data unexpressible. Plus the schema is read-only (no Mutation type)
/// and never exposes the household key.</summary>
public class GraphQLApiTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private void SeedProducts(string household, params string[] names)
    {
        _db.HouseholdId = household;
        using var db = _db.CreateDbContext();
        foreach (var name in names) db.Products.Add(new Product { Name = name, Category = Category.Pantry });
        db.SaveChanges();
    }

    private void SeedRecipe(string household, string name)
    {
        _db.HouseholdId = household;
        using var db = _db.CreateDbContext();
        db.Recipes.Add(new Recipe { Name = name, SavedAt = DateTimeOffset.Now });
        db.SaveChanges();
    }

    // ── Tenancy: the resolvers scope through IHouseholdDbFactory ──────────────────────────────────────

    [Fact]
    public async Task Products_returns_only_the_scoped_households_products()
    {
        SeedProducts("hh-a", "Alpha Milk", "Alpha Bread");
        SeedProducts("hh-b", "Beta Secret");

        _db.HouseholdId = "hh-a";
        var a = await new Query().GetProducts(_db, default);
        Assert.Equal(["Alpha Bread", "Alpha Milk"], a.Select(p => p.Name)); // name-ordered, hh-a only

        _db.HouseholdId = "hh-b";
        var b = await new Query().GetProducts(_db, default);
        Assert.Equal(["Beta Secret"], b.Select(p => p.Name));
    }

    [Fact]
    public async Task Product_by_a_foreign_households_id_is_null_not_an_oracle()
    {
        SeedProducts("hh-b", "Beta Secret");
        int betaId;
        _db.HouseholdId = "hh-b";
        await using (var db = _db.CreateDbContext())
        {
            betaId = (await db.Products.SingleAsync()).Id;
        }

        // Scoped to hh-a, a lookup of hh-b's id comes back null — the query filter scopes it out, so it's
        // indistinguishable from an id that never existed (no cross-household read, no existence oracle).
        _db.HouseholdId = "hh-a";
        Assert.Null(await new Query().GetProduct(betaId, _db, default));

        // And within its own household it resolves — proving the null above is scoping, not a broken query.
        _db.HouseholdId = "hh-b";
        Assert.NotNull(await new Query().GetProduct(betaId, _db, default));
    }

    [Fact]
    public async Task Recipes_returns_only_the_scoped_households_recipes()
    {
        SeedRecipe("hh-a", "Alpha Stew");
        SeedRecipe("hh-b", "Beta Roast");

        _db.HouseholdId = "hh-a";
        var a = await new Query().GetRecipes(_db, default);
        Assert.Equal(["Alpha Stew"], a.Select(r => r.Name));
    }

    // ── Shape: read-only, and the household key is never exposed ──────────────────────────────────────

    [Fact]
    public async Task The_schema_is_read_only_and_hides_the_household_key()
    {
        var schema = await new ServiceCollection()
            .AddSingleton<IHouseholdDbFactory>(_db)
            .AddPantryGraphQL()
            .BuildSchemaAsync();

        // Read-only by ABSENCE — no Mutation type exists, so an external client can never write.
        Assert.Null(schema.MutationType);

        var roots = schema.QueryType.Fields.Select(f => f.Name).ToList();
        Assert.Contains("products", roots);
        Assert.Contains("product", roots);
        Assert.Contains("recipes", roots);

        // The Product type exposes name but NOT the tenancy key (explicit field binding — a domain
        // property added later can't auto-leak it either).
        var product = schema.Types.OfType<ObjectType>().Single(t => t.Name == "Product");
        var productFields = product.Fields.Select(f => f.Name).ToList();
        Assert.Contains("name", productFields);
        Assert.DoesNotContain("householdId", productFields);
    }

    // ── End to end through Hot Chocolate's in-memory executor ─────────────────────────────────────────

    [Fact]
    public async Task A_products_query_executes_and_returns_only_the_scoped_household()
    {
        SeedProducts("hh-a", "Alpha Milk");
        SeedProducts("hh-b", "Beta Secret");
        _db.HouseholdId = "hh-a";

        await using var provider = new ServiceCollection()
            .AddSingleton<IHouseholdDbFactory>(_db)
            .AddPantryGraphQL()
            .Services
            .BuildServiceProvider();
        var executor = await provider.GetRequestExecutorAsync();

        var result = await executor.ExecuteAsync("{ products { name } }");
        var json = result.ToJson();

        Assert.Contains("Alpha Milk", json);
        Assert.DoesNotContain("Beta Secret", json); // the other household's data is unreachable
        Assert.DoesNotContain("errors", json);      // the resolver ran cleanly through the scoped factory
    }
}
