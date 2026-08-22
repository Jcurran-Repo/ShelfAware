using HotChocolate;
using HotChocolate.Types;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Shopping;
using ShelfAware.Web.GraphQL.DataLoaders;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a <see cref="Product"/>. Fields are bound EXPLICITLY (opt-in), so the
/// exposed surface is exactly this list and a domain property added later can never auto-leak — in
/// particular <c>HouseholdId</c> (the tenancy key) stays hidden. Core can't carry GraphQL attributes
/// (it has no Hot Chocolate reference), so the surface is controlled here in Web.
///
/// <c>prediction</c> and <c>estimate</c> are RESOLVERS that run the pure Core engine on read (never
/// stored), through <see cref="PantryReadContext"/> so both share one memoized PredictionResult ("one
/// prediction, one story"). <c>tags</c> and <c>substitutes</c> are DataLoader-batched (the N+1 fix — they
/// aren't eager-loaded by the root query).</summary>
public sealed class ProductType : ObjectType<Product>
{
    protected override void Configure(IObjectTypeDescriptor<Product> descriptor)
    {
        descriptor.BindFieldsExplicitly();
        descriptor.Field(p => p.Id);
        descriptor.Field(p => p.Name);
        descriptor.Field(p => p.Category);
        descriptor.Field(p => p.DefaultUnit);
        descriptor.Field(p => p.TrackQuantity);
        descriptor.Field(p => p.QuantityOnHand);
        descriptor.Field(p => p.QuantityCountedAt);
        descriptor.Field(p => p.Purchases);

        // Computed by the engine on read, with the product-surface flags. Both go through
        // PantryReadContext.PredictAsync, which memoizes per product, so the estimate's embedded
        // prediction is the SAME object as the prediction field beside it.
        descriptor.Field("prediction")
            .Type<PredictionType>()
            .Resolve(ctx => ctx.Service<PantryReadContext>().PredictAsync(ctx.Parent<Product>()));

        descriptor.Field("estimate")
            .Type<EstimateType>()
            .Resolve(async ctx =>
            {
                var read = ctx.Service<PantryReadContext>();
                var product = ctx.Parent<Product>();
                var prediction = await read.PredictAsync(product);
                var prices = await read.PriceIndexAsync();
                return ShoppingEstimator.For(product, prediction, read.Today,
                    prices.PriceFor(product.Id, prediction.RecommendedSize));
            });

        // Not eager-loaded by the root query (which would N+1 / cartesian-explode) — batched per request.
        descriptor.Field("tags")
            .Type<ListType<ProductTagType>>()
            .Resolve(async ctx =>
                await ctx.DataLoader<TagsByProductDataLoader>().LoadAsync(ctx.Parent<Product>().Id, ctx.RequestAborted)
                ?? []);

        descriptor.Field("substitutes")
            .Type<ListType<ProductSubstituteType>>()
            .Resolve(async ctx =>
                await ctx.DataLoader<SubstitutesByProductDataLoader>().LoadAsync(ctx.Parent<Product>().Id, ctx.RequestAborted)
                ?? []);
    }
}
