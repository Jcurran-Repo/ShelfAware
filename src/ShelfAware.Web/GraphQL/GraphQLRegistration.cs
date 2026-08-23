using HotChocolate.Execution.Configuration;
using ShelfAware.Web.GraphQL.DataLoaders;

namespace ShelfAware.Web.GraphQL;

/// <summary>THE one registration of the read-only pantry GraphQL schema — the root query, every explicit
/// object type, the per-request read context, and the DataLoaders. Program.cs (the live endpoint) and the
/// schema/execution tests both call this, so the schema they build can't drift. Security limits (phase 5)
/// hang further calls off the returned builder.</summary>
public static class GraphQLRegistration
{
    /// <param name="includeExceptionDetails">Whether resolver exception detail (message + stack) rides in
    /// the GraphQL error response. Dev only — defaults to the prod-safe false so a forgotten call site
    /// masks rather than leaks (the repo's "stop leaking ex.Message" rule).</param>
    public static IRequestExecutorBuilder AddPantryGraphQL(this IServiceCollection services, bool includeExceptionDetails = false)
    {
        // Scoped: one per GraphQL request, so "today", the expiration setting, and the price index are
        // each derived once and the prediction memoized (the "one prediction, one story" guarantee).
        services.AddScoped<PantryReadContext>();

        return services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddType<ProductType>()
            .AddType<PurchaseEventType>()
            .AddType<RecipeType>()
            .AddType<RecipeIngredientType>()
            .AddType<RecipeStepType>()
            .AddType<RecipeTagType>()
            .AddType<PredictionType>()
            .AddType<EstimateType>()
            .AddType<ProductTagType>()
            .AddType<ProductSubstituteType>()
            .AddDataLoader<TagsByProductDataLoader>()
            .AddDataLoader<SubstitutesByProductDataLoader>()
            // Security limits (phase 5). Depth: reject pathologically nested queries — belt-and-suspenders
            // here (the schema has no type cycles, so a data query can't nest deep), but it future-proofs
            // the guard and bounds deep introspection. Cost: cap the total field cost so one query can't
            // fan out into a DoS; generous ceilings that legitimate queries stay well under.
            .AddMaxExecutionDepthRule(15)
            .ModifyCostOptions(o =>
            {
                o.EnforceCostLimits = true;
                o.MaxFieldCost = 10_000;
                o.MaxTypeCost = 100_000;
            })
            // Mask resolver exception detail outside Development, so a thrown error never leaks a message
            // or stack to an API client (matches the repo's audit rule).
            .ModifyRequestOptions(o => o.IncludeExceptionDetails = includeExceptionDetails);
    }
}
