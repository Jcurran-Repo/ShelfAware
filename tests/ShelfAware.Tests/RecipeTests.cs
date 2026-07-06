using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

// The behaviour that moved onto the Recipe / RecipeIngredient entities (makeability, the main/seasoning
// split, variant identity). The fuzzy covering rules themselves are IngredientMatcher's job and tested
// there; these pin the entity-level aggregation and delegation.
public class RecipeTests
{
    private static PantryProduct P(string name, params string[] alsoWorksAs) => new(name, alsoWorksAs);
    private static RecipeIngredient Main(string name) => new() { Name = name, IsMain = true };
    private static RecipeIngredient Seasoning(string name) => new() { Name = name, IsMain = false };

    private static Recipe ChickenAndPotatoes() => new()
    {
        Name = "Chicken & Potatoes",
        Ingredients = [Main("chicken breast"), Main("potatoes"), Seasoning("salt"), Seasoning("black pepper")],
    };

    [Fact]
    public void An_original_recipe_is_not_a_variant()
    {
        Assert.False(new Recipe { Name = "Roast Chicken" }.IsVariant);
    }

    [Fact]
    public void A_recipe_with_a_parent_is_a_variant_of_that_parent()
    {
        var original = new Recipe { Id = 7, Name = "Original" };
        var variant = new Recipe { Name = "Adapted", ParentRecipeId = 7 };

        Assert.True(variant.IsVariant);
        Assert.True(variant.IsVariantOf(original));
        Assert.False(variant.IsVariantOf(new Recipe { Id = 8, Name = "Someone else" }));
    }

    [Fact]
    public void Mains_and_seasonings_split_by_the_flag()
    {
        var r = ChickenAndPotatoes();
        Assert.Equal(["chicken breast", "potatoes"], r.MainIngredients.Select(i => i.Name));
        Assert.Equal(["salt", "black pepper"], r.Seasonings.Select(i => i.Name));
    }

    [Fact]
    public void An_ingredient_is_satisfied_when_something_on_hand_covers_it()
    {
        var ingredient = Main("chicken breast");
        Assert.True(ingredient.IsSatisfiedBy([P("Chicken Breast Tenderloins")]));
        Assert.False(ingredient.IsSatisfiedBy([P("Whole Chicken")]));
    }

    [Fact]
    public void A_recipe_is_makeable_only_when_every_main_is_on_hand()
    {
        var r = ChickenAndPotatoes();

        // Seasonings are never part of the check — chicken + potatoes is enough.
        Assert.True(r.IsMakeableWith([P("Chicken Breast Tenderloins"), P("Fresh Baby Potatoes")]));
        Assert.False(r.IsMakeableWith([P("Chicken Breast Tenderloins")])); // no potatoes
    }

    [Fact]
    public void A_recipe_with_no_main_ingredients_is_never_makeable()
    {
        var r = new Recipe { Name = "Seasoning blend", Ingredients = [Seasoning("salt"), Seasoning("pepper")] };
        Assert.False(r.IsMakeableWith([P("Salt"), P("Pepper")]));
    }

    [Fact]
    public void MissingMains_lists_only_the_uncovered_main_ingredients()
    {
        var r = ChickenAndPotatoes();
        var missing = r.MissingMains([P("Chicken Breast Tenderloins")]).Select(i => i.Name).ToList();
        Assert.Equal(["potatoes"], missing);
    }
}
