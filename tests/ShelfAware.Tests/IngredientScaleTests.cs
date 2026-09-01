using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

/// <summary>
/// The serving-box amount scaler: parse the leading ASCII amount, scale it, reformat with cooking-friendly
/// fractions, keep the unit/tail, and leave anything numberless alone. Serving ratios are small integers,
/// so the realistic factors are halves/thirds/quarters and whole multiples.
/// </summary>
public class IngredientScaleTests
{
    [Theory]
    // Whole multiples up and down.
    [InlineData("2 lbs", 2.0, "4 lbs")]
    [InlineData("3 cloves", 2.0, "6 cloves")]
    [InlineData("2 cups", 0.5, "1 cup")]
    [InlineData("2 lbs", 0.5, "1 lb")]
    [InlineData("1 lb", 2.0, "2 lbs")]
    // Fractions in.
    [InlineData("1/2 cup", 2.0, "1 cup")]
    [InlineData("1/4 cup", 2.0, "1/2 cup")]
    [InlineData("1 1/2 tsp", 2.0, "3 tsp")]
    [InlineData("1 1/2 cups", 2.0, "3 cups")]
    // Fractions out.
    [InlineData("1 cup", 0.5, "1/2 cup")]
    [InlineData("3 cloves", 0.5, "1 1/2 cloves")]
    // Thirds (serves 3 → serves 1, or → serves 2).
    [InlineData("3 cloves", 0.3333333333, "1 clove")]
    [InlineData("1 cup", 0.6666666667, "2/3 cup")]
    // Ranges keep their separator verbatim.
    [InlineData("2-3 cloves", 2.0, "4-6 cloves")]
    [InlineData("2 to 3 cups", 2.0, "4 to 6 cups")]
    [InlineData("2 - 3 tbsp", 2.0, "4 - 6 tbsp")]
    // Decimals, incl. a leading ".5".
    [InlineData("2.5 cups", 2.0, "5 cups")]
    [InlineData(".5 cup", 2.0, "1 cup")]
    // Bare number, no unit.
    [InlineData("2", 2.0, "4")]
    [InlineData("1", 3.0, "3")]
    public void Scales_the_leading_amount(string quantity, double factor, string expected) =>
        Assert.Equal(expected, IngredientScale.Scale(quantity, factor));

    [Theory]
    // Every friendly fraction, produced as OUTPUT — so the snap table can't drift on any entry.
    [InlineData(0.125, "1/8 cup")]
    [InlineData(0.25, "1/4 cup")]
    [InlineData(1.0 / 3, "1/3 cup")]
    [InlineData(0.375, "3/8 cup")]
    [InlineData(0.5, "1/2 cup")]
    [InlineData(0.625, "5/8 cup")]
    [InlineData(2.0 / 3, "2/3 cup")]
    [InlineData(0.75, "3/4 cup")]
    [InlineData(0.875, "7/8 cup")]
    public void Snaps_a_scaled_value_to_the_nearest_cooking_fraction(double factor, string expected) =>
        Assert.Equal(expected, IngredientScale.Scale("1 cup", factor));

    [Theory]
    // Measure abbreviations never take a plural "s"…
    [InlineData("2 tsp", "4 tsp")]
    [InlineData("2 tbsp", "4 tbsp")]
    [InlineData("2 oz", "4 oz")]
    [InlineData("2 ml", "4 ml")]
    [InlineData("2 l", "4 l")]
    [InlineData("2 g", "4 g")]
    [InlineData("2 kg", "4 kg")]
    // …nor do size/prep adjectives ("2 large onions", never "2 larges").
    [InlineData("2 large", "4 large")]
    [InlineData("2 medium", "4 medium")]
    [InlineData("2 small", "4 small")]
    [InlineData("2 whole", "4 whole")]
    public void Invariant_words_do_not_take_a_plural_s(string quantity, string expected) =>
        Assert.Equal(expected, IngredientScale.Scale(quantity, 2.0));

    [Theory]
    // Full-word units inflect to match the scaled count, including lb↔lbs and the sibilant "-es" plurals.
    [InlineData("1 cup", 2.0, "2 cups")]
    [InlineData("1 pinch", 2.0, "2 pinches")]
    [InlineData("1 dash", 3.0, "3 dashes")]
    [InlineData("1 glass", 2.0, "2 glasses")]   // ends "s" → "-es" (and the "ss" stem is kept whole)
    [InlineData("1 box", 2.0, "2 boxes")]       // ends "x" → "-es"
    [InlineData("2 dishes", 0.5, "1 dish")]     // "-es" singularised back
    [InlineData("2 pinches", 0.5, "1 pinch")]
    [InlineData("2 glass", 0.5, "1 glass")]     // "ss" stem is not over-trimmed to "glas"
    public void Full_word_units_pluralise_and_singularise(string quantity, double factor, string expected) =>
        Assert.Equal(expected, IngredientScale.Scale(quantity, factor));

    [Theory]
    [InlineData("to taste")]
    [InlineData("a pinch")]
    [InlineData("salt")]
    [InlineData("for garnish")]
    [InlineData("½ cup")]   // a unicode fraction has no leading ASCII number — left as written, not guessed
    public void Leaves_an_unparseable_amount_verbatim(string quantity) =>
        Assert.Equal(quantity, IngredientScale.Scale(quantity, 2.0));

    [Theory]
    [InlineData(1.0)]                       // a factor of 1 is a no-op
    [InlineData(0.0)]                       // non-positive never emits "0…"
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_no_op_or_invalid_factor_returns_the_amount_unchanged(double factor) =>
        Assert.Equal("2 cups", IngredientScale.Scale("2 cups", factor));

    [Fact]
    public void Null_and_blank_pass_through()
    {
        Assert.Null(IngredientScale.Scale(null, 2.0));
        Assert.Equal("   ", IngredientScale.Scale("   ", 2.0));
    }

    [Fact]
    public void Only_the_leading_amount_scales_not_a_number_inside_the_tail()
    {
        // The "(14 oz)" is a package spec, left literal; only the leading count scales, and "can" pluralises.
        Assert.Equal("2 (14 oz) cans", IngredientScale.Scale("1 (14 oz) can", 2.0));
        Assert.Equal("3 (14 oz) cans", IngredientScale.Scale("1 (14 oz) can", 3.0));
    }

    [Fact]
    public void A_value_that_snaps_to_no_friendly_fraction_falls_back_to_a_decimal() =>
        Assert.Equal("2.19 oz", IngredientScale.Scale("10 oz", 0.219));

    [Fact]
    public void A_zero_denominator_fraction_keeps_the_numerator_rather_than_dividing_by_zero() =>
        Assert.Equal("2 cups", IngredientScale.Scale("1/0 cup", 2.0));

    [Fact]
    public void A_two_letter_unit_ending_in_s_is_handled_without_over_trimming() =>
        // Exercises the sibilant check on an empty stem (Singularize("es") → "e"), the defensive guard there.
        Assert.Equal("1 e", IngredientScale.Scale("2 es", 0.5));

    [Fact]
    public void Leading_whitespace_is_preserved() =>
        Assert.Equal(" 4 cups", IngredientScale.Scale(" 2 cups", 2.0));

    [Fact]
    public void A_non_plural_word_ending_in_a_sibilant_run_is_not_over_singularised() =>
        // "sixty" doesn't end in "-es", so it must not be trimmed to "six" when scaled down to one.
        Assert.Equal("1 sixty", IngredientScale.Scale("2 sixty", 0.5));

    [Fact]
    public void Scaling_down_to_a_small_amount_shows_a_decimal_never_zero() =>
        // A batch recipe's main scaled down to one serving must not read "0 cup".
        Assert.Equal("0.04 cup", IngredientScale.Scale("1/2 cup", 0.08));

    [Fact]
    public void A_trace_amount_floors_to_a_visible_minimum_rather_than_zero() =>
        Assert.Equal("0.01 oz", IngredientScale.Scale("1 oz", 0.001));

    [Fact]
    public void Zero_stays_zero() =>
        Assert.Equal("0 cup", IngredientScale.Scale("0 cup", 2.0));

    [Fact]
    public void The_unit_plural_follows_the_displayed_number_not_the_raw_value() =>
        // 1.033 snaps to the displayed "1", so the unit must read singular ("1 cup", never "1 cups").
        Assert.Equal("1 cup", IngredientScale.Scale("1 cup", 31.0 / 30));

    [Fact]
    public void A_large_amount_scales_without_throwing() =>
        Assert.Equal("20000000001 cups", IngredientScale.Scale("10000000000 1/2 cups", 2.0));

    [Fact]
    public void A_pathologically_large_amount_is_left_unscaled_rather_than_producing_garbage()
    {
        var absurd = new string('9', 400) + " cups";
        Assert.Equal(absurd, IngredientScale.Scale(absurd, 2.0));
    }
}
