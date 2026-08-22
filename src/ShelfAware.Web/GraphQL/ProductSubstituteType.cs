using HotChocolate.Types;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a <see cref="ProductSubstitute"/> — the "also works as" phrase this
/// product can stand in for. Just its text; the household key, id, and back-reference stay hidden.</summary>
public sealed class ProductSubstituteType : ObjectType<ProductSubstitute>
{
    protected override void Configure(IObjectTypeDescriptor<ProductSubstitute> descriptor)
    {
        descriptor.BindFieldsExplicitly();
        descriptor.Field(s => s.Value);
    }
}
