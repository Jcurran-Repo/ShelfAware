using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

/// <summary>
/// The library dedup key: a generated recipe's identity is its normalized name + its set of main-ingredient
/// foods, so the same dish regenerated maps to one recipe while genuinely different dishes don't merge.
/// </summary>
public class RecipeSignatureTests
{
    [Fact]
    public void Same_name_and_mains_share_a_signature_regardless_of_ingredient_order()
    {
        Assert.Equal(
            RecipeSignature.Of("Skillet Beef Tacos", ["ground beef", "flour tortillas"]),
            RecipeSignature.Of("Skillet Beef Tacos", ["flour tortillas", "ground beef"]));
    }

    [Fact]
    public void A_different_name_gives_a_different_signature()
    {
        Assert.NotEqual(
            RecipeSignature.Of("Beef Tacos", ["ground beef"]),
            RecipeSignature.Of("Beef Burritos", ["ground beef"]));
    }

    [Fact]
    public void Different_main_ingredients_give_a_different_signature()
    {
        Assert.NotEqual(
            RecipeSignature.Of("Tacos", ["ground beef"]),
            RecipeSignature.Of("Tacos", ["chicken breast"]));
    }

    [Fact]
    public void The_name_is_folded_for_case_and_punctuation()
    {
        Assert.Equal(
            RecipeSignature.Of("Home-Canned Tomato Sauce", ["tomatoes"]),
            RecipeSignature.Of("home canned tomato sauce", ["tomatoes"]));
    }

    [Fact]
    public void Trivial_ingredient_modifiers_do_not_change_the_signature()
    {
        // "fresh" / "boneless" are trivial modifiers the matcher already ignores — same food, same signature.
        Assert.Equal(
            RecipeSignature.Of("Roast", ["chicken breast"]),
            RecipeSignature.Of("Roast", ["fresh boneless chicken breast"]));
    }
}
