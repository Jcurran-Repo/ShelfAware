using HotChocolate.Types;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a <see cref="PurchaseEvent"/> — one buying occasion. Explicitly bound
/// (no <c>HouseholdId</c>, no <c>ProductId</c>/<c>Product</c> back-reference, no <c>ReceiptId</c>/
/// <c>Receipt</c> nav — those are internal plumbing). Price isn't here: <see cref="PurchaseEvent"/>
/// carries none (it lives on the receipt line); the expected cost is surfaced via the computed
/// <c>estimate</c> field in phase 4.</summary>
public sealed class PurchaseEventType : ObjectType<PurchaseEvent>
{
    protected override void Configure(IObjectTypeDescriptor<PurchaseEvent> descriptor)
    {
        descriptor.BindFieldsExplicitly();
        descriptor.Field(p => p.PurchasedAt);
        descriptor.Field(p => p.Quantity);
        descriptor.Field(p => p.Brand);
        descriptor.Field(p => p.Size);
        descriptor.Field(p => p.Variety);
        descriptor.Field(p => p.ExpirationDate);
        descriptor.Field(p => p.Source);
    }
}
