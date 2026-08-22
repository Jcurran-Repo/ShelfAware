using HotChocolate.Types;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a <see cref="ProductTag"/> — just its text (mirrors RecipeTagType). The
/// household key, id, and product back-reference stay off the schema.</summary>
public sealed class ProductTagType : ObjectType<ProductTag>
{
    protected override void Configure(IObjectTypeDescriptor<ProductTag> descriptor)
    {
        descriptor.BindFieldsExplicitly();
        descriptor.Field(t => t.Value);
    }
}
