using System.Text.RegularExpressions;

namespace ShelfAware.Core.Speech;

/// <summary>
/// Rewrites written text into the form a text-to-speech model should read aloud — fractions, unit
/// abbreviations, temperatures, ranges and dimensions spelled out as words.
///
/// This exists because the TTS model does no normalization for us. ElevenLabs' own guidance is that
/// Flash v2.5 struggles with numbers (their example: it reads "$1,000,000" as "one thousand thousand
/// dollars" where Multilingual v2 gets it right); normalization is off by default on Flash to protect
/// its ~75 ms latency, and forcing it on via apply_text_normalization is an Enterprise-plan feature.
/// Their recommendation for everyone else is to send text that is already spelled out — so we do it
/// here: plain code, no model, no per-character cost, and testable (thesis: plain code where it suffices).
///
/// Scope is deliberately tuned to our input, which is recipe prose written by the recipe advisor —
/// well-formed sentences using standard abbreviations ("Simmer 6-7 min/side", "1/2 tsp cumin",
/// "Preheat to 350°F"). It is not a general-purpose normalizer for arbitrary human text.
/// </summary>
public static class SpeechText
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>
    /// Abbreviation (and already-spelled) measure words → the singular/plural forms to speak. Words
    /// that are already spelled out map to themselves so the fraction rules below can still reach them
    /// ("1/2 cup" → "half a cup"). Deliberately EXCLUDES ambiguous single letters ("c" = cup or Celsius,
    /// "t" = teaspoon or tablespoon, "l" = liter, "in" = the preposition) — a wrong expansion is worse
    /// than an unexpanded one, and the advisor doesn't emit them.
    /// </summary>
    private static readonly Dictionary<string, (string Singular, string Plural)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tsp"] = ("teaspoon", "teaspoons"),
        ["tsps"] = ("teaspoon", "teaspoons"),
        ["teaspoon"] = ("teaspoon", "teaspoons"),
        ["teaspoons"] = ("teaspoon", "teaspoons"),
        ["tbsp"] = ("tablespoon", "tablespoons"),
        ["tbsps"] = ("tablespoon", "tablespoons"),
        ["tbs"] = ("tablespoon", "tablespoons"),
        ["tablespoon"] = ("tablespoon", "tablespoons"),
        ["tablespoons"] = ("tablespoon", "tablespoons"),
        ["cup"] = ("cup", "cups"),
        ["cups"] = ("cup", "cups"),
        ["oz"] = ("ounce", "ounces"),
        ["ounce"] = ("ounce", "ounces"),
        ["ounces"] = ("ounce", "ounces"),
        ["lb"] = ("pound", "pounds"),
        ["lbs"] = ("pound", "pounds"),
        ["pound"] = ("pound", "pounds"),
        ["pounds"] = ("pound", "pounds"),
        ["g"] = ("gram", "grams"),
        ["kg"] = ("kilogram", "kilograms"),
        ["mg"] = ("milligram", "milligrams"),
        ["ml"] = ("milliliter", "milliliters"),
        ["qt"] = ("quart", "quarts"),
        ["pt"] = ("pint", "pints"),
        ["gal"] = ("gallon", "gallons"),
        ["cm"] = ("centimeter", "centimeters"),
        ["mm"] = ("millimeter", "millimeters"),
        ["inch"] = ("inch", "inches"),
        ["inches"] = ("inch", "inches"),
        ["min"] = ("minute", "minutes"),
        ["mins"] = ("minute", "minutes"),
        ["minute"] = ("minute", "minutes"),
        ["minutes"] = ("minute", "minutes"),
        ["hr"] = ("hour", "hours"),
        ["hrs"] = ("hour", "hours"),
        ["hour"] = ("hour", "hours"),
        ["hours"] = ("hour", "hours"),
        ["sec"] = ("second", "seconds"),
        ["secs"] = ("second", "seconds"),
    };

    /// <summary>Spoken forms of a fraction standing alone vs. immediately qualifying a unit — English
    /// wants "one half" but "half a teaspoon", not "one half teaspoon".</summary>
    private static readonly Dictionary<string, (string Alone, string BeforeUnit)> Fractions = new(StringComparer.Ordinal)
    {
        ["1/2"] = ("one half", "half a"),
        ["1/3"] = ("one third", "a third of a"),
        ["2/3"] = ("two thirds", "two thirds of a"),
        ["1/4"] = ("one quarter", "a quarter of a"),
        ["3/4"] = ("three quarters", "three quarters of a"),
        ["1/5"] = ("one fifth", "a fifth of a"),
        ["2/5"] = ("two fifths", "two fifths of a"),
        ["3/5"] = ("three fifths", "three fifths of a"),
        ["4/5"] = ("four fifths", "four fifths of a"),
        ["1/8"] = ("one eighth", "an eighth of a"),
        ["3/8"] = ("three eighths", "three eighths of a"),
        ["5/8"] = ("five eighths", "five eighths of a"),
        ["7/8"] = ("seven eighths", "seven eighths of a"),
        ["1/6"] = ("one sixth", "a sixth of a"),
        ["5/6"] = ("five sixths", "five sixths of a"),
        ["1/16"] = ("one sixteenth", "a sixteenth of a"),
    };

    /// <summary>The fraction part of a mixed number ("1 1/2 cups" → "one and a half cups").</summary>
    private static readonly Dictionary<string, string> MixedFractions = new(StringComparer.Ordinal)
    {
        ["1/2"] = "and a half",
        ["1/3"] = "and a third",
        ["2/3"] = "and two thirds",
        ["1/4"] = "and a quarter",
        ["3/4"] = "and three quarters",
        ["1/8"] = "and an eighth",
    };

    private static readonly Dictionary<string, string> UnicodeFractions = new(StringComparer.Ordinal)
    {
        ["½"] = "1/2", ["⅓"] = "1/3", ["⅔"] = "2/3", ["¼"] = "1/4", ["¾"] = "3/4",
        ["⅕"] = "1/5", ["⅖"] = "2/5", ["⅗"] = "3/5", ["⅘"] = "4/5",
        ["⅙"] = "1/6", ["⅚"] = "5/6", ["⅛"] = "1/8", ["⅜"] = "3/8", ["⅝"] = "5/8", ["⅞"] = "7/8",
    };

    private static readonly string[] SmallNumbers =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
         "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
         "nineteen", "twenty"];

    // Longest-first so "tbsp" is preferred over "tbs" and "mins" over "min" at the same position.
    private static readonly string UnitPattern =
        string.Join("|", Units.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));

    // Fahrenheit tolerates a missing degree sign ("350F", "350 F") because a bare F after a number is
    // unambiguous. Celsius REQUIRES the sign: "2 C flour" means two cups, not two Celsius.
    private static readonly Regex Fahrenheit = new(@"(\d+)\s*(?:°\s*)?F\b", Options);
    private static readonly Regex Celsius = new(@"(\d+)\s*°\s*C\b", Options);
    private static readonly Regex BareDegrees = new(@"(\d+)\s*°", Options);
    private static readonly Regex Dimensions = new(@"(\d+)\s*[x×]\s*(\d+)", Options);
    private static readonly Regex NumberRange = new(@"(\d+)\s*[-–]\s*(\d+)", Options);
    private static readonly Regex MixedWithUnit = new($@"\b(\d+)\s+(\d+/\d+)\s*({UnitPattern})\b", Options);
    private static readonly Regex MixedBare = new(@"\b(\d+)\s+(\d+/\d+)\b", Options);
    private static readonly Regex FractionWithUnit = new($@"(\d+/\d+)\s*({UnitPattern})\b", Options);
    private static readonly Regex NumberWithUnit = new($@"\b(\d+(?:\.\d+)?)\s*({UnitPattern})\b", Options);
    private static readonly Regex BareFraction = new(@"(\d+/\d+)", Options);
    private static readonly Regex DecimalPoint = new(@"\b(\d+)\.(\d+)\b", Options);
    // Only between letters — "1/2" must already be gone by the time this runs.
    private static readonly Regex WordSlashWord = new(@"(?<=[A-Za-z])\s*/\s*(?=[A-Za-z])", Options);
    private static readonly Regex ExtraSpace = new(@"[ \t]{2,}", Options);

    /// <summary>
    /// The text to send to TTS for <paramref name="text"/>. Returns an empty string for null/blank
    /// input. Order matters: fractions resolve before "/" becomes "per", and temperatures resolve
    /// before unit expansion so an F/C suffix isn't mistaken for a measure.
    /// </summary>
    public static string ForSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var s = text;

        foreach (var (glyph, ascii) in UnicodeFractions)
            s = s.Replace(glyph, " " + ascii);

        s = Fahrenheit.Replace(s, m => $"{m.Groups[1].Value} degrees Fahrenheit");
        s = Celsius.Replace(s, m => $"{m.Groups[1].Value} degrees Celsius");
        s = BareDegrees.Replace(s, m => $"{m.Groups[1].Value} degrees");
        s = Dimensions.Replace(s, m => $"{m.Groups[1].Value} by {m.Groups[2].Value}");
        s = NumberRange.Replace(s, m => $"{m.Groups[1].Value} to {m.Groups[2].Value}");

        s = MixedWithUnit.Replace(s, m =>
            MixedFractions.TryGetValue(m.Groups[2].Value, out var frac)
                ? $"{IntegerWord(m.Groups[1].Value)} {frac} {Speak(m.Groups[3].Value, plural: true)}"
                : m.Value);

        s = MixedBare.Replace(s, m =>
            MixedFractions.TryGetValue(m.Groups[2].Value, out var frac)
                ? $"{IntegerWord(m.Groups[1].Value)} {frac}"
                : m.Value);

        s = FractionWithUnit.Replace(s, m =>
            Fractions.TryGetValue(m.Groups[1].Value, out var frac)
                ? $"{frac.BeforeUnit} {Speak(m.Groups[2].Value, plural: false)}"
                : m.Value);

        s = NumberWithUnit.Replace(s, m =>
            $"{m.Groups[1].Value} {Speak(m.Groups[2].Value, plural: !IsOne(m.Groups[1].Value))}");

        s = BareFraction.Replace(s, m =>
            Fractions.TryGetValue(m.Groups[1].Value, out var frac) ? frac.Alone : m.Value);

        s = DecimalPoint.Replace(s, m => $"{m.Groups[1].Value} point {m.Groups[2].Value}");
        s = WordSlashWord.Replace(s, " per ");

        return ExtraSpace.Replace(s, " ").Trim();
    }

    private static string Speak(string unit, bool plural) =>
        Units.TryGetValue(unit, out var forms) ? (plural ? forms.Plural : forms.Singular) : unit;

    private static bool IsOne(string quantity) =>
        decimal.TryParse(quantity, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n == 1m;

    private static string IntegerWord(string digits) =>
        int.TryParse(digits, out var n) && n >= 0 && n < SmallNumbers.Length ? SmallNumbers[n] : digits;
}
