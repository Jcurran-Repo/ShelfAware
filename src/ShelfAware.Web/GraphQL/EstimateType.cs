using HotChocolate.Types;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a <see cref="ProductEstimate"/> — the shopping view (buy how many, of
/// what size, at what expected cost) built from the prediction plus confirmed prices. Explicitly bound to
/// the client-facing fields; the internal helpers (OnePackage, TypicalQuantity, the Brands/Varieties
/// breakdown, the redundant ProductId/Name/Category) stay off the schema. <c>countNote</c> is the ONE
/// shared phrasing for a count-suppressed row (the engine's own words), exposed so a client renders the
/// same explanation the app does.</summary>
public sealed class EstimateType : ObjectType<ProductEstimate>
{
    protected override void Configure(IObjectTypeDescriptor<ProductEstimate> descriptor)
    {
        descriptor.Name("Estimate");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(e => e.Status);
        descriptor.Field(e => e.Basis);
        descriptor.Field(e => e.LastPurchased);
        descriptor.Field(e => e.NextBuyDate);
        descriptor.Field(e => e.DaysUntil);
        descriptor.Field(e => e.RecommendedQuantity);
        descriptor.Field(e => e.RecommendedSize);
        descriptor.Field(e => e.UnitPrice);
        descriptor.Field(e => e.ExpectedCost);
        descriptor.Field(e => e.UsualBrand);
        descriptor.Field(e => e.UsualVariety);
        descriptor.Field(e => e.SuppressedByCount);
        descriptor.Field(e => e.CountOnHand);
        descriptor.Field(e => e.CountedOn);
        descriptor.Field(e => e.CountNote);
    }
}
