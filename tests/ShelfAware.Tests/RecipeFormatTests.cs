using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

// The one definition of how an ingredient reads on a line, shared by the Recipes page, the Cookbook,
// and the printed products list (RecipeFormat.Line).
public class RecipeFormatTests
{
    [Fact]
    public void Line_prefixes_the_amount_when_the_recipe_gives_one()
    {
        Assert.Equal("2 lbs chicken breast", RecipeFormat.Line("2 lbs", "chicken breast"));
    }

    [Fact]
    public void Line_is_just_the_name_when_there_is_no_amount()
    {
        Assert.Equal("chicken breast", RecipeFormat.Line(null, "chicken breast"));
        Assert.Equal("chicken breast", RecipeFormat.Line("   ", "chicken breast"));
    }
}
