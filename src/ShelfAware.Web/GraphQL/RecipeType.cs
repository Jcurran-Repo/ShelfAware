using HotChocolate.Types;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a saved <see cref="Recipe"/>. Explicitly bound, so the household key and
/// the domain BEHAVIOUR members (IsMakeableWith / MissingMains / Uses — methods that take an on-hand set
/// and would otherwise surface as fields with complex input arguments) stay off the schema. The raw
/// <c>ImagePath</c> (a disk path) is hidden; the photo is reached through the existing household-scoped
/// <c>/api/recipe-image/{id}</c> endpoint.</summary>
public sealed class RecipeType : ObjectType<Recipe>
{
    protected override void Configure(IObjectTypeDescriptor<Recipe> descriptor)
    {
        descriptor.BindFieldsExplicitly();
        descriptor.Field(r => r.Id);
        descriptor.Field(r => r.Name);
        descriptor.Field(r => r.Blurb);
        descriptor.Field(r => r.SavedAt);
        descriptor.Field(r => r.TimesEaten);
        descriptor.Field(r => r.EstimatedCaloriesPerServing);
        descriptor.Field(r => r.IsVariant);
        descriptor.Field(r => r.ParentRecipeId);
        descriptor.Field(r => r.Ingredients);
        descriptor.Field(r => r.Steps);
        descriptor.Field(r => r.Tags);
    }
}
