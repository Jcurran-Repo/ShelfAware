using HotChocolate.Execution.Configuration;

namespace ShelfAware.Web.GraphQL;

/// <summary>THE one registration of the read-only pantry GraphQL schema — the root query plus every
/// explicit object type. Program.cs (the live endpoint) and the schema/execution tests both call this,
/// so the schema they build can't drift. Computed fields + DataLoaders (phase 4) and security limits
/// (phase 5) hang further calls off the returned builder.</summary>
public static class GraphQLRegistration
{
    public static IRequestExecutorBuilder AddPantryGraphQL(this IServiceCollection services) =>
        services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddType<ProductType>()
            .AddType<PurchaseEventType>()
            .AddType<RecipeType>()
            .AddType<RecipeIngredientType>()
            .AddType<RecipeStepType>()
            .AddType<RecipeTagType>();
}
