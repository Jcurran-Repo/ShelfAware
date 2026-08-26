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

    // ---- Every dictionary entry, pinned. These strings ARE what the reader says, so an exhaustive
    //      sweep over each table is the honest coverage: one typo'd expansion is one mispronounced word.

    [Theory]
    [InlineData("tsp", "teaspoon", "teaspoons")]
    [InlineData("tsps", "teaspoon", "teaspoons")]
    [InlineData("teaspoon", "teaspoon", "teaspoons")]
    [InlineData("teaspoons", "teaspoon", "teaspoons")]
    [InlineData("tbsp", "tablespoon", "tablespoons")]
    [InlineData("tbsps", "tablespoon", "tablespoons")]
    [InlineData("tbs", "tablespoon", "tablespoons")]
    [InlineData("tablespoon", "tablespoon", "tablespoons")]
    [InlineData("tablespoons", "tablespoon", "tablespoons")]
    [InlineData("cup", "cup", "cups")]
    [InlineData("cups", "cup", "cups")]
    [InlineData("oz", "ounce", "ounces")]
    [InlineData("ounce", "ounce", "ounces")]
    [InlineData("ounces", "ounce", "ounces")]
    [InlineData("lb", "pound", "pounds")]
    [InlineData("lbs", "pound", "pounds")]
    [InlineData("pound", "pound", "pounds")]
    [InlineData("pounds", "pound", "pounds")]
    [InlineData("g", "gram", "grams")]
    [InlineData("kg", "kilogram", "kilograms")]
    [InlineData("mg", "milligram", "milligrams")]
    [InlineData("ml", "milliliter", "milliliters")]
    [InlineData("qt", "quart", "quarts")]
    [InlineData("pt", "pint", "pints")]
    [InlineData("gal", "gallon", "gallons")]
    [InlineData("cm", "centimeter", "centimeters")]
    [InlineData("mm", "millimeter", "millimeters")]
    [InlineData("inch", "inch", "inches")]
    [InlineData("inches", "inch", "inches")]
    [InlineData("min", "minute", "minutes")]
    [InlineData("mins", "minute", "minutes")]
    [InlineData("minute", "minute", "minutes")]
    [InlineData("minutes", "minute", "minutes")]
    [InlineData("hr", "hour", "hours")]
    [InlineData("hrs", "hour", "hours")]
    [InlineData("hour", "hour", "hours")]
    [InlineData("hours", "hour", "hours")]
    [InlineData("sec", "second", "seconds")]
    [InlineData("secs", "second", "seconds")]
    public void Every_unit_speaks_singular_at_one_and_plural_above(string abbr, string singular, string plural)
    {
        Assert.Equal($"1 {singular} here", SpeechText.ForSpeech($"1 {abbr} here"));
        Assert.Equal($"3 {plural} here", SpeechText.ForSpeech($"3 {abbr} here"));
    }

    [Theory]
    [InlineData("1/2", "one half", "half a")]
    [InlineData("1/3", "one third", "a third of a")]
    [InlineData("2/3", "two thirds", "two thirds of a")]
    [InlineData("1/4", "one quarter", "a quarter of a")]
    [InlineData("3/4", "three quarters", "three quarters of a")]
    [InlineData("1/5", "one fifth", "a fifth of a")]
    [InlineData("2/5", "two fifths", "two fifths of a")]
    [InlineData("3/5", "three fifths", "three fifths of a")]
    [InlineData("4/5", "four fifths", "four fifths of a")]
    [InlineData("1/8", "one eighth", "an eighth of a")]
    [InlineData("3/8", "three eighths", "three eighths of a")]
    [InlineData("5/8", "five eighths", "five eighths of a")]
    [InlineData("7/8", "seven eighths", "seven eighths of a")]
    [InlineData("1/6", "one sixth", "a sixth of a")]
    [InlineData("5/6", "five sixths", "five sixths of a")]
    [InlineData("1/16", "one sixteenth", "a sixteenth of a")]
    public void Every_fraction_speaks_alone_and_before_a_unit(string frac, string alone, string beforeUnit)
    {
        Assert.Equal($"add {alone} here", SpeechText.ForSpeech($"add {frac} here"));  // not before a unit
        Assert.Equal($"{beforeUnit} cup", SpeechText.ForSpeech($"{frac} cup"));       // qualifying a unit
    }

    [Theory]
    [InlineData("1/2", "and a half")]
    [InlineData("1/3", "and a third")]
    [InlineData("2/3", "and two thirds")]
    [InlineData("1/4", "and a quarter")]
    [InlineData("3/4", "and three quarters")]
    [InlineData("1/8", "and an eighth")]
    public void Every_mixed_fraction_reads_after_the_whole_number(string frac, string spoken) =>
        Assert.Equal($"two {spoken} cups", SpeechText.ForSpeech($"2 {frac} cups"));

    [Theory]
    [InlineData("½", "one half")]        // ½
    [InlineData("⅓", "one third")]       // ⅓
    [InlineData("⅔", "two thirds")]      // ⅔
    [InlineData("¼", "one quarter")]     // ¼
    [InlineData("¾", "three quarters")]  // ¾
    [InlineData("⅕", "one fifth")]       // ⅕
    [InlineData("⅖", "two fifths")]      // ⅖
    [InlineData("⅗", "three fifths")]    // ⅗
    [InlineData("⅘", "four fifths")]     // ⅘
    [InlineData("⅙", "one sixth")]       // ⅙
    [InlineData("⅚", "five sixths")]     // ⅚
    [InlineData("⅛", "one eighth")]      // ⅛
    [InlineData("⅜", "three eighths")]   // ⅜
    [InlineData("⅝", "five eighths")]    // ⅝
    [InlineData("⅞", "seven eighths")]   // ⅞
    public void Every_unicode_fraction_decodes_then_speaks(string glyph, string alone) =>
        Assert.Equal($"add {alone} here", SpeechText.ForSpeech($"add {glyph} here"));

    [Theory]
    [InlineData("0", "zero")]
    [InlineData("1", "one")]
    [InlineData("2", "two")]
    [InlineData("3", "three")]
    [InlineData("4", "four")]
    [InlineData("5", "five")]
    [InlineData("6", "six")]
    [InlineData("7", "seven")]
    [InlineData("8", "eight")]
    [InlineData("9", "nine")]
    [InlineData("10", "ten")]
    [InlineData("11", "eleven")]
    [InlineData("12", "twelve")]
    [InlineData("13", "thirteen")]
    [InlineData("14", "fourteen")]
    [InlineData("15", "fifteen")]
    [InlineData("16", "sixteen")]
    [InlineData("17", "seventeen")]
    [InlineData("18", "eighteen")]
    [InlineData("19", "nineteen")]
    [InlineData("20", "twenty")]
    [InlineData("21", "21")]  // past SmallNumbers (index 20) — the whole number stays as digits
    public void The_whole_number_of_a_mixed_number_reads_as_a_word(string n, string word) =>
        Assert.Equal($"{word} and a half cups", SpeechText.ForSpeech($"{n} 1/2 cups"));

    [Fact]
    public void A_bare_mixed_number_reads_without_a_unit() =>
        // MixedBare: a mixed number with no unit after it. "reduce to 1 1/2, then rest".
        Assert.Equal("reduce to one and a half, then rest", SpeechText.ForSpeech("reduce to 1 1/2, then rest"));

    [Fact]
    public void A_unicode_fraction_glued_to_a_whole_number_still_separates() =>
        // The space prefixed to the decoded fraction splits "1½" into the mixed number "1 1/2", not the
        // nonsense "11/2". Common in recipes ("1½ cups").
        Assert.Equal("one and a half cups flour", SpeechText.ForSpeech("1½ cups flour"));
}
