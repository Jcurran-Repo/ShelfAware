using ShelfAware.Core.Speech;

namespace ShelfAware.Tests;

/// <summary>
/// Pins the spoken form of recipe prose. These aren't cosmetic: the TTS model does no normalization
/// for us (see <see cref="SpeechText"/>), so whatever these assert is literally what the reader says.
/// </summary>
public class SpeechTextTests
{
    [Theory]
    [InlineData("1/2 tsp cumin", "half a teaspoon cumin")]
    [InlineData("1/4 tsp salt", "a quarter of a teaspoon salt")]
    [InlineData("1/2 cup rice", "half a cup rice")]
    [InlineData("3/4 cup sugar", "three quarters of a cup sugar")]
    [InlineData("1/3 cup milk", "a third of a cup milk")]
    [InlineData("2/3 cup broth", "two thirds of a cup broth")]
    [InlineData("1/8 tsp cayenne", "an eighth of a teaspoon cayenne")]
    public void Fractions_qualifying_a_unit_read_naturally(string input, string expected) =>
        Assert.Equal(expected, SpeechText.ForSpeech(input));

    [Theory]
    [InlineData("1 1/2 cups flour", "one and a half cups flour")]
    [InlineData("2 1/2 lbs chicken", "two and a half pounds chicken")]
    [InlineData("1 1/4 cups water", "one and a quarter cups water")]
    [InlineData("3 3/4 cups stock", "three and three quarters cups stock")]
    public void Mixed_numbers_read_as_words(string input, string expected) =>
        Assert.Equal(expected, SpeechText.ForSpeech(input));

    [Theory]
    [InlineData("2 tbsp olive oil", "2 tablespoons olive oil")]
    [InlineData("1 tbsp butter", "1 tablespoon butter")]
    [InlineData("1 lb ground beef", "1 pound ground beef")]
    [InlineData("2 lbs potatoes", "2 pounds potatoes")]
    [InlineData("8 oz cream cheese", "8 ounces cream cheese")]
    [InlineData("500 g flour", "500 grams flour")]
    [InlineData("250 ml cream", "250 milliliters cream")]
    public void Unit_abbreviations_expand_and_agree_in_number(string input, string expected) =>
        Assert.Equal(expected, SpeechText.ForSpeech(input));

    [Theory]
    [InlineData("Preheat to 350°F", "Preheat to 350 degrees Fahrenheit")]
    [InlineData("Preheat to 350F", "Preheat to 350 degrees Fahrenheit")]
    [InlineData("Preheat to 350 F", "Preheat to 350 degrees Fahrenheit")]
    [InlineData("Heat to 180°C", "Heat to 180 degrees Celsius")]
    [InlineData("Rest at 40°", "Rest at 40 degrees")]
    public void Temperatures_are_spoken_as_degrees(string input, string expected) =>
        Assert.Equal(expected, SpeechText.ForSpeech(input));

    // "2 C flour" is two CUPS, not two Celsius — so Celsius requires the degree sign. A wrong
    // expansion is worse than none.
    [Fact]
    public void A_bare_C_after_a_number_is_not_treated_as_celsius() =>
        Assert.Equal("Add 2 C flour", SpeechText.ForSpeech("Add 2 C flour"));

    // An F that merely starts the next word is not a temperature.
    [Fact]
    public void An_f_starting_a_word_is_not_fahrenheit() =>
        Assert.Equal("5 Fresh basil leaves", SpeechText.ForSpeech("5 Fresh basil leaves"));

    [Theory]
    [InlineData("Simmer 6-7 min/side", "Simmer 6 to 7 minutes per side")]
    [InlineData("Sear 4-5 min per side", "Sear 4 to 5 minutes per side")]
    [InlineData("Bake 20-25 min at 400F", "Bake 20 to 25 minutes at 400 degrees Fahrenheit")]
    [InlineData("Cook 350-400°F", "Cook 350 to 400 degrees Fahrenheit")]
    public void Ranges_and_per_side_read_as_prose(string input, string expected) =>
        Assert.Equal(expected, SpeechText.ForSpeech(input));

    [Theory]
    [InlineData("Bake in a 9x13 pan", "Bake in a 9 by 13 pan")]
    [InlineData("Use an 8x8 dish", "Use an 8 by 8 dish")]
    public void Dimensions_read_as_by(string input, string expected) =>
        Assert.Equal(expected, SpeechText.ForSpeech(input));

    [Theory]
    [InlineData("Simmer for 1.5 hours", "Simmer for 1 point 5 hours")]
    [InlineData("Add 2.5 lbs beef", "Add 2 point 5 pounds beef")]
    public void Decimals_are_spoken_with_point(string input, string expected) =>
        Assert.Equal(expected, SpeechText.ForSpeech(input));

    [Theory]
    [InlineData("½ tsp salt", "half a teaspoon salt")]
    [InlineData("¾ cup milk", "three quarters of a cup milk")]
    [InlineData("Add ⅔ of the sauce", "Add two thirds of the sauce")]
    public void Unicode_fractions_are_decoded_before_speaking(string input, string expected) =>
        Assert.Equal(expected, SpeechText.ForSpeech(input));

    [Theory]
    [InlineData("Cut into 1/4-inch dice", "Cut into one quarter-inch dice")]
    [InlineData("Reduce by 1/2", "Reduce by one half")]
    public void Standalone_fractions_use_the_alone_form(string input, string expected) =>
        Assert.Equal(expected, SpeechText.ForSpeech(input));

    // A date, a plain sentence, and an already-spelled quantity must survive untouched.
    [Theory]
    [InlineData("Season the chicken and set it aside.")]
    [InlineData("Stir until the sauce thickens.")]
    [InlineData("Add 2 cups water")]
    public void Ordinary_prose_is_left_alone(string input) =>
        Assert.Equal(input, SpeechText.ForSpeech(input));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Blank_input_yields_blank_output(string? input, string expected) =>
        Assert.Equal(expected, SpeechText.ForSpeech(input));

    // The whole point, end to end: one realistic advisor-written step.
    [Fact]
    public void A_realistic_recipe_step_reads_cleanly()
    {
        var actual = SpeechText.ForSpeech(
            "Preheat oven to 425°F. Toss 1 1/2 lbs potatoes with 2 tbsp oil and 1/2 tsp salt, " +
            "spread on a 9x13 sheet, and roast 25-30 min, flipping halfway.");

        Assert.Equal(
            "Preheat oven to 425 degrees Fahrenheit. Toss one and a half pounds potatoes with " +
            "2 tablespoons oil and half a teaspoon salt, spread on a 9 by 13 sheet, and roast " +
            "25 to 30 minutes, flipping halfway.",
            actual);
    }
}
