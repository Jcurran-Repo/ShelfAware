using HotChocolate.Types;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a <see cref="Product"/>. Fields are bound EXPLICITLY (opt-in), so the
/// exposed surface is exactly this list and a domain property added later can never auto-leak — in
/// particular <c>HouseholdId</c> (the tenancy key) stays hidden. Core can't carry GraphQL attributes
/// (it has no Hot Chocolate reference), so the surface is controlled here in Web.
///
/// <c>tags</c>, <c>substitutes</c>, <c>prediction</c>, and <c>estimate</c> are added in phase 4 (the
/// first two via DataLoaders, the last two via the Core engine) — deliberately NOT exposed here, because
/// they aren't eager-loaded/computed yet and a silently-empty field would be worse than an absent one.</summary>
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
    }
}
