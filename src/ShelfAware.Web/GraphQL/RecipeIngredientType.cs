using HotChocolate.Types;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a <see cref="RecipeIngredient"/>. Explicitly bound: the household key,
/// the recipe back-reference, the <c>AlternativesJson</c> cache (internal), and the makeability method
/// stay hidden. <c>matchedProduct</c> is the product this ingredient mapped to at save time — the honest
/// "recipes that use X" link.</summary>
public sealed class RecipeIngredientType : ObjectType<RecipeIngredient>
{
    protected override void Configure(IObjectTypeDescriptor<RecipeIngredient> descriptor)
    {
        descriptor.BindFieldsExplicitly();
        descriptor.Field(i => i.Name);
        descriptor.Field(i => i.Quantity);
        descriptor.Field(i => i.IsMain);
        descriptor.Field(i => i.MatchedProduct);
    }
}
