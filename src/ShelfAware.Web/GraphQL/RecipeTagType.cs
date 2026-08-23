using HotChocolate.Types;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a <see cref="RecipeTag"/> — just its text. Explicitly bound so the
/// household key, the id, and the recipe back-reference stay off the schema.</summary>
public sealed class RecipeTagType : ObjectType<RecipeTag>
{
    protected override void Configure(IObjectTypeDescriptor<RecipeTag> descriptor)
    {
        descriptor.BindFieldsExplicitly();
        descriptor.Field(t => t.Value);
    }
}
