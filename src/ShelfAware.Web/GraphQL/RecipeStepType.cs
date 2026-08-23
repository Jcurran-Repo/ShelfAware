using HotChocolate.Types;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a <see cref="RecipeStep"/> — one ordered line of the method. Explicitly
/// bound (no household key, no recipe back-reference).</summary>
public sealed class RecipeStepType : ObjectType<RecipeStep>
{
    protected override void Configure(IObjectTypeDescriptor<RecipeStep> descriptor)
    {
        descriptor.BindFieldsExplicitly();
        descriptor.Field(s => s.Order);
        descriptor.Field(s => s.Text);
    }
}
