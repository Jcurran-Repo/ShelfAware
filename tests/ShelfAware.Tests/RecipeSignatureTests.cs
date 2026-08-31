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

    // The exact format, pinned so the key stays stable and its parts can't bleed together: normalized name,
    // then "|", then the ingredient foods (each its own words sorted, ingredients sorted, comma-joined).
    [Fact]
    public void The_signature_has_a_stable_name_pipe_sorted_ingredients_format()
    {
        Assert.Equal("tacos|beef,cheese", RecipeSignature.Of("Tacos", ["cheese", "beef"]));
    }

    [Fact]
    public void An_ingredients_words_are_sorted_and_space_joined()
    {
        Assert.Equal("x|breast chicken", RecipeSignature.Of("X", ["chicken breast"]));
    }

    [Fact]
    public void A_null_name_gives_an_empty_name_key()
    {
        Assert.Equal("|beef", RecipeSignature.Of(null, ["beef"]));
    }

    [Fact]
    public void An_ingredient_with_no_food_words_is_dropped()
    {
        // "fresh" is all trivial modifiers → no core words → it contributes nothing (not an empty key).
        Assert.Equal("x|beef", RecipeSignature.Of("X", ["beef", "fresh"]));
    }
}
