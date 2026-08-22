using HotChocolate.Execution.Configuration;
using ShelfAware.Web.GraphQL.DataLoaders;

namespace ShelfAware.Web.GraphQL;

/// <summary>THE one registration of the read-only pantry GraphQL schema — the root query, every explicit
/// object type, the per-request read context, and the DataLoaders. Program.cs (the live endpoint) and the
/// schema/execution tests both call this, so the schema they build can't drift. Security limits (phase 5)
/// hang further calls off the returned builder.</summary>
public static class GraphQLRegistration
{
    public static IRequestExecutorBuilder AddPantryGraphQL(this IServiceCollection services)
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
            .AddDataLoader<SubstitutesByProductDataLoader>();
    }
}
