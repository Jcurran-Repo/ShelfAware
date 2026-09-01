using System.Globalization;
using System.Text.RegularExpressions;

namespace ShelfAware.Core.Recipes;

/// <summary>
/// Scales the free-text amount on a recipe ingredient by a factor — the pure, instant (no-AI) core behind
/// the meal-plan card's servings box. It parses the LEADING amount ("2 lbs" → "4 lbs", "1/2 cup" → "1 cup",
/// "1 (14 oz) can" → "2 (14 oz) cans", "2-3 cloves" → "4-6 cloves"), scales it, and reformats it with
/// cooking-friendly fractions, leaving the unit and any trailing text intact. An amount with no leading
/// number — "a pinch", "to taste", "salt" — is returned UNCHANGED: honest about what it can't scale, the
/// same stance <see cref="Domain.RecipeIngredient.Quantity"/> already takes ("display guidance only").
/// <para>ASCII amounts only (integer, decimal, fraction, mixed number) — the meal-plan generator writes
/// amounts that way; a unicode "½" is left verbatim rather than guessed at. This is NOT the cross-product
/// unit arithmetic the app forbids for makeability — it's scalar scaling of one written amount, display-only,
/// exactly what the field is for. Only the LEADING amount scales; a number inside the tail ("(14 oz)") is a
/// package spec and is left verbatim.</para>
/// <para>English-only and deliberately naive on pluralisation — the sibling of <see
/// cref="Shopping.QuantityFormat"/>'s naive singular trim. It normalises the amount's trailing unit word to
/// match the scaled value ("1 cup" ↔ "2 cups", "3 cloves" → "6 cloves"), leaving known measure
/// abbreviations (tsp, oz, ml…) invariant. Irregular plurals ("leaf"→"leafs") are accepted rough edges.</para>
/// </summary>
public static class IngredientScale
{
    private const double FracTolerance = 0.05;

    // Cooking-friendly fractions to snap a scaled value's fractional part to (halves, thirds, quarters,
    // eighths). The "" entries are the round-to-whole sentinels: 0 rounds down, 1 carries up.
    private static readonly (double Value, string Text, int Carry)[] Fractions =
    [
        (0d, "", 0), (1d / 8, "1/8", 0), (1d / 4, "1/4", 0), (1d / 3, "1/3", 0), (3d / 8, "3/8", 0),
        (1d / 2, "1/2", 0), (5d / 8, "5/8", 0), (2d / 3, "2/3", 0), (3d / 4, "3/4", 0), (7d / 8, "7/8", 0),
        (1d, "", 1),
    ];

    // Words that don't take a plural "s" when the count changes: measure abbreviations ("2 tsp", not
    // "2 tsps") and size/prep adjectives ("2 large" onions, not "2 larges"). Full-word units (cup, clove,
    // can, pound…) DO inflect through the naive rules below, including "lb" ↔ "lbs".
    private static readonly HashSet<string> InvariantUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "tsp", "tbsp", "oz", "ml", "l", "g", "kg",
        "large", "medium", "small", "whole",
    };

    // The leading amount: a mixed number ("1 1/2"), a fraction ("1/2"), a decimal ("2.5", ".5"), or an
    // integer ("2") — optionally a range ("2-3", "2 to 3"). Anchored to the start; the rest is the tail.
    private static readonly Regex LeadingAmount = new(
        @"^(?<lead>\s*)(?<a>\d+\s+\d+/\d+|\d+/\d+|\d*\.\d+|\d+)(?:(?<sep>\s*-\s*|\s+to\s+)(?<b>\d+\s+\d+/\d+|\d+/\d+|\d*\.\d+|\d+))?");

    // Runs of letters in the tail — the last one is the unit noun to pluralise/singularise.
    private static readonly Regex LetterRun = new("[A-Za-z]+");

    /// <summary>The amount scaled by <paramref name="factor"/>, or the amount unchanged when it carries no
    /// leading ASCII number, when <paramref name="factor"/> is 1, or when it's null/blank. A non-positive or
    /// non-finite factor is a no-op; a positive amount that scales to a tiny value shows a small decimal
    /// (floored to a visible 0.01), never a bare "0" or a negative.</summary>
    public static string? Scale(string? quantity, double factor)
    {
        if (string.IsNullOrWhiteSpace(quantity)) return quantity;
        if (!double.IsFinite(factor) || factor <= 0d || factor == 1d) return quantity;

        var m = LeadingAmount.Match(quantity);
        if (!m.Success) return quantity; // no leading amount → leave "to taste" / "a pinch" verbatim

        var lead = m.Groups["lead"].Value;
        var tail = quantity[m.Length..];
        var a = ParseAmount(m.Groups["a"].Value) * factor;
        if (!double.IsFinite(a)) return quantity; // a pathological (hallucinated) amount can't scale

        if (m.Groups["b"].Success)
        {
            var b = ParseAmount(m.Groups["b"].Value) * factor;
            if (!double.IsFinite(b)) return quantity;
            var (aText, _) = Format(a);
            var (bText, bValue) = Format(b);
            return lead + aText + m.Groups["sep"].Value + bText + Inflect(tail, bValue);
        }
        var (text, value) = Format(a);
        return lead + text + Inflect(tail, value);
    }

    // "1 1/2" → 1.5, "1/2" → 0.5, "2.5"/".5"/"2" → their value. The regex guarantees the shape; parsing as
    // double (never int) means an absurdly large digit run becomes Infinity rather than throwing — the
    // caller then leaves the amount unscaled.
    private static double ParseAmount(string token)
    {
        if (token.Contains('/'))
        {
            var parts = token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var frac = ParseFraction(parts[^1]);
            return parts.Length == 2 ? double.Parse(parts[0], CultureInfo.InvariantCulture) + frac : frac;
        }
        return double.Parse(token, CultureInfo.InvariantCulture);
    }

    private static double ParseFraction(string frac)
    {
        var slash = frac.IndexOf('/');
        var num = double.Parse(frac[..slash], CultureInfo.InvariantCulture);
        var den = double.Parse(frac[(slash + 1)..], CultureInfo.InvariantCulture);
        return den == 0 ? num : num / den; // "1/0" can't sanely scale — keep the numerator
    }

    // Format a scaled value as a cooking amount — an integer, a whole+fraction ("1 1/2"), a bare fraction
    // ("3/4"), or a trimmed 2-dp decimal — AND report the effective numeric value it represents, so the
    // unit's plural/singular form (in Inflect) follows the number actually shown rather than the raw
    // pre-snap value (e.g. 1.03 displays "1" and reads singular, not "1 cups").
    private static (string Text, double Value) Format(double v)
    {
        var whole = (long)Math.Floor(v);
        var frac = v - whole;
        var best = Fractions.MinBy(c => Math.Abs(frac - c.Value));
        // Stryker disable once equality: `<=` vs `<` differ only when the distance equals FracTolerance
        // exactly. It never can: 0.05 has no exact double and every candidate is a simple rational, so
        // |frac - candidate| is never exactly 0.05 — the boundary point is unreachable.
        if (Math.Abs(frac - best.Value) <= FracTolerance)
        {
            var w = whole + best.Carry;
            if (best.Text.Length == 0)
            {
                // A positive value that snaps to a bare whole of 0 is a tiny amount, not "none" — show a
                // small decimal (floored to a visible 0.01), never "0 cup", and let plurality follow it.
                if (w == 0 && v > 0d)
                {
                    var small = Math.Max(Math.Round(v, 2), 0.01);
                    return (small.ToString(CultureInfo.InvariantCulture), small);
                }
                return (w.ToString(CultureInfo.InvariantCulture), w);
            }
            return (w == 0 ? best.Text : $"{w} {best.Text}", w + best.Value);
        }
        var rounded = Math.Round(v, 2); // Round already caps at 2 dp
        return (rounded.ToString(CultureInfo.InvariantCulture), rounded);
    }

    // Normalise the LAST word of the tail (the unit noun) to singular/plural for the scaled value:
    // "1 cup" ↔ "2 cups", "3 cloves" → "6 cloves", "1 (14 oz) can" → "2 (14 oz) cans". Measure
    // abbreviations stay invariant; a tail with no letters (a bare "4") is left alone.
    private static string Inflect(string tail, double value)
    {
        var words = LetterRun.Matches(tail);
        if (words.Count == 0) return tail;

        var last = words[^1];
        var word = last.Value;
        if (InvariantUnits.Contains(word)) return tail;

        var stem = Singularize(word);
        var target = Math.Round(value, 3) > 1d ? Pluralize(stem) : stem;
        // When target == word this rebuilds the identical tail, so no special-case is needed.
        return tail[..last.Index] + target + tail[(last.Index + last.Length)..];
    }

    private static string Singularize(string word)
    {
        if (word.EndsWith("es", StringComparison.OrdinalIgnoreCase) && EndsWithSibilant(word[..^2]))
            return word[..^2];
        if (word.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !word.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            return word[..^1];
        return word;
    }

    private static string Pluralize(string stem) => EndsWithSibilant(stem) ? stem + "es" : stem + "s";

    private static bool EndsWithSibilant(string w) =>
        w.Length > 0 && (char.ToLowerInvariant(w[^1]) is 's' or 'x'
            || w.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || w.EndsWith("sh", StringComparison.OrdinalIgnoreCase));
}
