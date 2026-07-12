using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

public class IngredientMatcherTests
{
    private static PantryProduct P(string name, params string[] alsoWorksAs) => new(name, alsoWorksAs);

    private static readonly PantryProduct[] Pantry =
    [
        P("Chicken Breast Tenderloins"),
        P("Fresh Baby Yellow Potatoes"),
        P("Mixed Bell Peppers"),
        P("Lean Ground Beef"),
    ];

    [Theory]
    [InlineData("chicken breast")]          // right there in the product name
    [InlineData("Boneless chicken breasts")] // trivial modifier + plural
    [InlineData("4 oz of chicken breast")]   // amount + unit stripped
    public void A_specific_cut_is_covered_by_a_product_whose_name_contains_it(string ingredient)
    {
        Assert.True(IngredientMatcher.IsSatisfied(ingredient, matchedProduct: null, Pantry));
    }

    [Theory]
    [InlineData("potatoes")]     // plural -> "Fresh Baby Yellow Potatoes"
    [InlineData("bell pepper")]  // -> "Mixed Bell Peppers"
    [InlineData("ground beef")]  // -> "Lean Ground Beef" (ground is kept, not stripped)
    public void Trivial_modifiers_and_plurals_are_tolerated(string ingredient)
    {
        Assert.True(IngredientMatcher.IsSatisfied(ingredient, matchedProduct: null, Pantry));
    }

    [Theory]
    [InlineData("Whole Chicken")]  // a whole roaster is not a breast — must NOT count
    [InlineData("Chicken Broth")]  // broth is not the meat
    public void A_different_form_of_the_same_food_does_not_cover_a_specific_cut(string productName)
    {
        Assert.False(IngredientMatcher.IsSatisfied("chicken breast", matchedProduct: null, [P(productName)]));
    }

    [Fact]
    public void Ground_beef_is_not_covered_by_a_steak()
    {
        Assert.False(IngredientMatcher.IsSatisfied("ground beef", matchedProduct: null, [P("Beef Steak")]));
    }

    [Theory]
    [InlineData("chicken breast", "Chicken Breasts", true)]              // plural + case
    [InlineData("chicken breast", "fresh chicken breast", true)]         // trivial modifier ignored
    [InlineData("chicken breast", "chicken breast tenderloins", false)]  // extra cut word = different form
    [InlineData("chicken breast", "chicken thighs", false)]
    public void IsSameFood_requires_mutual_coverage(string a, string b, bool expected)
    {
        Assert.Equal(expected, IngredientMatcher.IsSameFood(a, b));
    }

    [Fact]
    public void A_substitute_list_bridges_cuts_the_name_alone_would_miss()
    {
        // Only thighs on hand, but the user marked them as working for a breast recipe.
        Assert.True(IngredientMatcher.IsSatisfied(
            "chicken breast", matchedProduct: null, [P("Chicken Thighs", "chicken breast", "chicken cutlet")]));

        // A whole chicken that only lists generic "chicken" still won't cover a specific breast.
        Assert.False(IngredientMatcher.IsSatisfied(
            "chicken breast", matchedProduct: null, [P("Whole Chicken", "chicken", "roast chicken")]));
    }

    [Fact]
    public void An_exact_matched_product_still_counts()
    {
        Assert.True(IngredientMatcher.IsSatisfied("Half & Half", matchedProduct: "Half & Half", [P("Half & Half")]));
    }

    [Fact]
    public void A_matched_product_not_on_hand_and_no_cover_is_not_satisfied()
    {
        Assert.False(IngredientMatcher.IsSatisfied("Quinoa", matchedProduct: "Quinoa", Pantry));
    }

    [Fact]
    public void Nothing_on_hand_means_not_satisfied()
    {
        Assert.False(IngredientMatcher.IsSatisfied("chicken breast", matchedProduct: null, []));
    }

    [Fact]
    public void A_blank_ingredient_is_not_satisfied()
    {
        Assert.False(IngredientMatcher.IsSatisfied("  ", matchedProduct: null, Pantry));
    }

    [Theory]
    [InlineData("chicken thighs", new[] { "chicken thighs", "potatoes" }, true)]
    [InlineData("chicken thighs", new[] { "Boneless Chicken Thighs" }, true)]  // modifiers tolerated
    [InlineData("chicken thighs", new[] { "chicken tenderloins", "potatoes" }, false)] // a different cut
    [InlineData("chicken thighs", new string[0], false)]
    public void IsMentionedIn_checks_whether_a_form_appears_among_names(string form, string[] names, bool expected)
    {
        // Used by the adapt guard to confirm the model actually used the chosen swap.
        Assert.Equal(expected, IngredientMatcher.IsMentionedIn(form, names));
    }
}
