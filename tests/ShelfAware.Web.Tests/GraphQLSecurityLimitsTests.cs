using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;
using ShelfAware.Web.GraphQL;

namespace ShelfAware.Web.Tests;

/// <summary>Phase-5 security limits: read-only by absence (no Mutation type), a query-depth cap, and cost
/// enforcement. The per-token rate limit is endpoint middleware (live-verified, not exercised here — the
/// in-memory executor has no HTTP pipeline).</summary>
public class GraphQLSecurityLimitsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHouseholdDbFactory>(_db);
        services.AddSingleton<IAppSettings>(new FakeAppSettings());
        return services;
    }

    private static async Task<string> ExecuteAsync(IServiceProvider provider, string query)
    {
        var executor = await provider.GetRequestExecutorAsync();
        var result = await executor.ExecuteAsync(query);
        return result.ToJson();
    }

    [Fact]
    public async Task The_schema_has_no_mutation_type()
    {
        var schema = await BaseServices().AddPantryGraphQL().BuildSchemaAsync();
        Assert.Null(schema.MutationType); // read-only by absence — an external client can never write
    }

    [Fact]
    public async Task A_normal_query_is_within_the_depth_and_cost_limits()
    {
        _db.HouseholdId = "hh-a";
        await using var provider = BaseServices().AddPantryGraphQL().Services.BuildServiceProvider();

        var json = await ExecuteAsync(provider, "{ products { name prediction { status } estimate { nextBuyDate } } }");

        Assert.DoesNotContain("errors", json); // a real query sails under the ceilings
    }

    [Fact]
    public async Task An_over_deep_query_is_rejected()
    {
        // Override the (generous) production depth to a tiny one, then send a query nested past it — this
        // pins that the max-execution-depth rule is WIRED and enforcing, independent of the production
        // limit (the schema has no data cycles, so a real query can't reach 15). A stricter rule wins, so
        // chaining AddMaxExecutionDepthRule(2) after AddPantryGraphQL's 15 caps the effective depth at 2.
        _db.HouseholdId = "hh-a";
        await using var provider = BaseServices()
            .AddPantryGraphQL()
            .AddMaxExecutionDepthRule(2)
            .Services
            .BuildServiceProvider();

        // products(1) → purchases(2) → brand(3): three levels, past the depth-2 cap.
        var json = await ExecuteAsync(provider, "{ products { purchases { brand } } }");

        Assert.Contains("errors", json);
        Assert.Contains("depth", json, StringComparison.OrdinalIgnoreCase); // the max-depth rule, specifically
    }

    [Fact]
    public async Task Cost_enforcement_rejects_a_query_over_the_budget()
    {
        // Override the (generous) production budget to a tiny one, so even a small query exceeds it — this
        // pins that cost analysis is actually WIRED and enforcing, independent of the production ceiling.
        _db.HouseholdId = "hh-a";
        await using var provider = BaseServices()
            .AddPantryGraphQL()
            .ModifyCostOptions(o => o.MaxFieldCost = 1)
            .Services
            .BuildServiceProvider();

        var json = await ExecuteAsync(provider, "{ products { name prediction { status } } }");

        Assert.Contains("errors", json);
        Assert.Contains("cost", json, StringComparison.OrdinalIgnoreCase);
    }
}
