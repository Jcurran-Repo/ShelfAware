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

    // ---- Covering: WHICH products cover it, since a caller that must act needs to know ---------------

    [Fact]
    public void Covering_names_the_product_that_satisfied_it()
    {
        var covering = IngredientMatcher.Covering("chicken breast", matchedProduct: null, Pantry);

        Assert.Equal(["Chicken Breast Tenderloins"], covering.Select(p => p.Name));
    }

    [Fact]
    public void Covering_returns_every_product_that_covers_a_loose_ingredient()
    {
        // The rule is deliberately loose, so two cuts really do both cover "ground beef". A caller that
        // must pick ONE has to decide what to do about that — MealStock refuses rather than guessing.
        var pantry = new[] { P("Ground Beef Chuck"), P("Ground Beef Sirloin"), P("Chicken Breast Tenderloins") };

        var covering = IngredientMatcher.Covering("ground beef", matchedProduct: null, pantry);

        Assert.Equal(["Ground Beef Chuck", "Ground Beef Sirloin"], covering.Select(p => p.Name).Order());
    }

    [Fact]
    public void Covering_returns_the_grounded_product_ALONE_when_it_is_on_hand()
    {
        // The precedence lives here, so no caller has to re-implement it: a human confirmed this pairing,
        // so a pinned ingredient comes back as exactly one candidate and can never read as ambiguous.
        var pantry = new[] { P("Ground Beef Chuck"), P("Ground Beef Sirloin") };

        var covering = IngredientMatcher.Covering("ground beef", "Ground Beef Sirloin", pantry);

        Assert.Equal(["Ground Beef Sirloin"], covering.Select(p => p.Name));
    }

    [Fact]
    public void Covering_falls_back_to_the_core_words_when_the_grounded_product_is_absent()
    {
        // A link to something no longer on hand must not blind the matcher to what IS.
        var covering = IngredientMatcher.Covering("chicken breast", "Something We Sold Out Of", Pantry);

        Assert.Equal(["Chicken Breast Tenderloins"], covering.Select(p => p.Name));
    }

    [Fact]
    public void Covering_finds_a_curated_stand_in()
    {
        var pantry = new[] { P("Ground Turkey", "ground beef") };

        Assert.Equal(["Ground Turkey"],
            IngredientMatcher.Covering("ground beef", matchedProduct: null, pantry).Select(p => p.Name));
    }

    [Fact]
    public void IsSatisfied_and_Covering_can_never_disagree()
    {
        // IsSatisfied is defined as Covering().Count > 0, so a tick on a recipe row and the action taken on
        // its behalf are the same question asked once. Checked across the mismatching cases too.
        string?[] ingredients = ["chicken breast", "whole chicken", "ground beef", null, "", "  "];
        foreach (var ingredient in ingredients)
        {
            foreach (var matched in new string?[] { null, "Lean Ground Beef", "Gone" })
            {
                Assert.Equal(
                    IngredientMatcher.IsSatisfied(ingredient, matched, Pantry),
                    IngredientMatcher.Covering(ingredient, matched, Pantry).Count > 0);
            }
        }
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
    public void A_grounded_match_covers_a_product_whose_name_differs_only_in_PUNCTUATION()
    {
        // ⚠️ Finding P. MatchedProduct is a NAME captured at save time; a punctuation variant of the current
        // product name ("Half and Half" for "Half-and-Half") is the same product to every write-side guard,
        // so the grounded leg matches by IDENTITY, not raw equality. Chosen so the core-token fallback CANNOT
        // rescue it — "creamer" shares no word with "Half-and-Half" — so ONLY the identity match covers it;
        // a raw string.Equals returned nothing, leaving the ✓ tick and makeability blind on that product.
        Assert.True(IngredientMatcher.IsSatisfied("creamer", matchedProduct: "Half and Half", [P("Half-and-Half")]));
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

    [Fact]
    public void IsMentionedIn_of_a_blank_form_is_false()
    {
        // A form with no core words can't be "mentioned" in anything — the need.Count > 0 guard.
        Assert.False(IngredientMatcher.IsMentionedIn("  ", ["chicken thighs"]));
        Assert.False(IngredientMatcher.IsMentionedIn("of the", ["chicken thighs"])); // all-trivial words
    }

    [Fact]
    public void Every_trivial_modifier_reduces_out()
    {
        // The Trivial set is the documented tuning knob: each entry must drop so "<modifier> chicken" is
        // the same food as "chicken". Pins every word individually — a dropped entry would leave its
        // modifier as a core token and break the equality.
        string[] trivial =
        [
            "fresh", "frozen", "canned", "jarred", "dried", "dry", "raw", "cooked", "boneless", "skinless",
            "organic", "natural", "ripe", "unsalted", "salted", "extra", "virgin", "large", "small", "medium",
            "jumbo", "baby", "mini", "sliced", "diced", "chopped", "minced", "shredded", "grated", "crushed",
            "cubed", "oz", "ounce", "ounces", "lb", "lbs", "pound", "pounds", "cup", "cups", "clove", "cloves",
            "can", "cans", "package", "packages", "pack", "and", "of", "with", "in", "a", "an", "the", "or",
        ];

        foreach (var word in trivial)
            Assert.True(
                IngredientMatcher.IsSameFood("chicken", $"{word} chicken"),
                $"trivial modifier '{word}' should reduce out");
    }

    [Theory]
    [InlineData("berry", "berries")]      // ies -> y
    [InlineData("tomato", "tomatoes")]    // oes -> o
    [InlineData("glass", "glasses")]      // ses -> s(s)
    [InlineData("box", "boxes")]          // xes -> x
    [InlineData("peach", "peaches")]      // ches -> ch
    [InlineData("dish", "dishes")]        // shes -> sh
    [InlineData("pepper", "peppers")]     // plain s -> (drop)
    public void A_plural_matches_its_singular(string singular, string plural)
    {
        // The Singular() suffix rules: each plural form names the same food as its singular. Pins the
        // suffix strings and the branches of the ||-chain — a broken rule leaves the plural unmatched.
        Assert.True(IngredientMatcher.IsSameFood(singular, plural));
        Assert.True(IngredientMatcher.IsSameFood(plural, singular)); // and symmetrically
    }

    [Fact]
    public void A_double_s_word_is_not_singularized_into_a_different_word()
    {
        // The !EndsWith("ss") guard: "glass" must stay "glass" (not "glas"), so a glass of something is
        // not confused with a "glas" of anything. Pins that the plain-s rule spares "ss" endings.
        Assert.False(IngredientMatcher.IsSameFood("glass", "gla")); // sanity: unrelated
        Assert.True(IngredientMatcher.IsSameFood("glass noodles", "glass noodle")); // noodle(s) matches; glass stays glass
    }

    [Fact]
    public void A_mixed_letter_and_digit_token_is_kept_not_stripped_as_a_number()
    {
        // Only PURE numbers are stripped (the !t.All(char.IsDigit) filter), so "v8" stays a core word
        // and a V8-juice product covers a "v8" ingredient. Mutating All->Any would drop any token with
        // a digit and lose it.
        Assert.True(IngredientMatcher.IsSatisfied("v8", matchedProduct: null, [P("v8 juice")]));
    }

    [Fact]
    public void Punctuation_between_words_separates_them_into_tokens()
    {
        // Tokenize turns every non-alphanumeric run into a space, so "mac & cheese" is {mac, cheese} —
        // the same food as "mac cheese". If punctuation were kept (the conditional-always-true mutant),
        // "&" would survive as a spurious core token and the two would no longer match.
        Assert.True(IngredientMatcher.IsSameFood("mac cheese", "mac & cheese"));
    }

    [Fact]
    public void An_empty_matched_product_does_not_ground_match_an_empty_named_product()
    {
        // The `matchedProduct is { Length: > 0 }` guard: a blank grounded name must NOT enter the
        // grounded leg (where its empty identity key would match an empty-named product). It falls to
        // the core-word rule instead, which an empty-named product cannot satisfy.
        Assert.Empty(IngredientMatcher.Covering("chicken breast", matchedProduct: "", [P("")]));
    }

    [Fact]
    public void IsSameFood_needs_coverage_in_BOTH_directions()
    {
        // A more specific candidate covers the ingredient one way but not back, so it is NOT the same
        // food. Pins the AND between the two Covers checks — an OR would call these equal.
        Assert.False(IngredientMatcher.IsSameFood("chicken", "chicken breast"));
        Assert.False(IngredientMatcher.IsSameFood("chicken breast", "chicken"));
    }

    [Theory]
    [InlineData("ie", "ies")]   // a bare 3-letter "ies" uses the plain-s drop, not the ies->y rule
    [InlineData("oe", "oes")]   // a bare 3-letter "oes" likewise falls to plain-s (the -es chain needs length > 3)
    public void The_multi_letter_es_rules_apply_only_above_the_length_boundary(string singular, string plural)
    {
        // The `Length > 3` guards keep the multi-letter -es rules off the bare three-letter suffixes,
        // which fall through to the plain-s drop instead. Pins those boundaries.
        Assert.True(IngredientMatcher.IsSameFood(singular, plural));
    }
}
