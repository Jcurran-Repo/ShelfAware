namespace ShelfAware.Core.Recipes;

/// <summary>An on-hand product for makeability: its name plus the recipe ingredients it can stand in for
/// (its curated "also works as" list — e.g. "Chicken Breast Tenderloins" also works as "chicken breast",
/// "chicken cutlet"). The substitute list is what lets a specific recipe stay specific — with real cook
/// times — while still going green when you own a valid stand-in.</summary>
public record PantryProduct(string Name, IReadOnlyList<string> AlsoWorksAs);

/// <summary>
/// Decides whether a recipe ingredient is covered by something on hand. Recipes stay specific ("chicken
/// breast", not "chicken"), so cook times mean something; the flexibility lives in each product's curated
/// substitute list. An ingredient is covered when its core words are all present in an on-hand product's
/// NAME or one of that product's "also works as" phrases. Deliberately plain code: only trivial modifiers
/// (fresh/frozen/boneless/size/unit) are stripped — cut and form words (breast, thigh, ground, whole) are
/// kept, so "chicken breast" is NOT satisfied by "Whole Chicken" unless the user explicitly says so via a
/// substitute. Unit-tested; <see cref="Trivial"/> is the tuning knob.
/// </summary>
public static class IngredientMatcher
{
    // Words that describe state / size / unit / packaging but NOT the food's form — safe to ignore.
    // Cut / prep / form words (breast, thigh, tenderloin, ground, whole, ...) are deliberately NOT here:
    // they distinguish foods that cook differently, and the substitute list handles real interchangeability.
    private static readonly HashSet<string> Trivial = new(StringComparer.Ordinal)
    {
        "fresh", "frozen", "canned", "jarred", "dried", "dry", "raw", "cooked", "boneless", "skinless",
        "organic", "natural", "ripe", "unsalted", "salted", "extra", "virgin", "large", "small", "medium",
        "jumbo", "baby", "mini", "sliced", "diced", "chopped", "minced", "shredded", "grated", "crushed",
        "cubed",
        // Amounts / units / packaging, so "4 oz of chicken breast" still reduces to "chicken breast".
        "oz", "ounce", "ounces", "lb", "lbs", "pound", "pounds", "cup", "cups", "clove", "cloves", "can",
        "cans", "package", "packages", "pack",
        // Connectives.
        "and", "of", "with", "in", "a", "an", "the", "or",
    };

    /// <summary>True if this ingredient is covered by anything on hand — its exact matched product, an
    /// on-hand product of the same specific food, or a product that lists this ingredient as a substitute.</summary>
    public static bool IsSatisfied(string? ingredientName, string? matchedProduct, IReadOnlyCollection<PantryProduct> onHand)
    {
        // The grounded product captured at save time still wins when it's actually on hand.
        if (matchedProduct is { Length: > 0 } &&
            onHand.Any(p => string.Equals(p.Name, matchedProduct, StringComparison.OrdinalIgnoreCase)))
            return true;

        var need = CoreTokens(ingredientName);
        if (need.Count == 0) return false;
        return onHand.Any(p => Covers(need, p.Name) || p.AlsoWorksAs.Any(s => Covers(need, s)));
    }

    // Every core word of the ingredient must appear (plural-tolerant) in the candidate phrase, so the
    // candidate is at least as specific as the ingredient: "chicken breast" is covered by "chicken breast
    // tenderloins" and by the substitute "chicken breast", but not by "whole chicken" or "chicken broth".
    private static bool Covers(IReadOnlyCollection<string> need, string candidate)
    {
        var cand = Tokenize(candidate);
        return need.All(n => cand.Any(c => TokenMatches(n, c)));
    }

    /// <summary>True if <paramref name="form"/> (e.g. "chicken thighs") is present among any of
    /// <paramref name="names"/> — the core words of the form all appear in one of them. Used to verify an
    /// adapt actually honored a chosen swap (and elsewhere a phrase is really referenced).</summary>
    public static bool IsMentionedIn(string form, IEnumerable<string> names)
    {
        var need = CoreTokens(form);
        return need.Count > 0 && names.Any(n => Covers(need, n));
    }

    // The core food words of an ingredient name, with trivial modifiers and bare numbers removed.
    internal static List<string> CoreTokens(string? name) =>
        Tokenize(name).Where(t => !Trivial.Contains(t) && !t.All(char.IsDigit)).ToList();

    private static bool TokenMatches(string a, string b) => a == b || Singular(a) == Singular(b);

    // Crude but sufficient singularizer for food nouns: potatoes->potato, peppers->pepper, berries->berry.
    private static string Singular(string t)
    {
        if (t.Length > 3 && t.EndsWith("ies")) return t[..^3] + "y";
        if (t.Length > 3 && (t.EndsWith("oes") || t.EndsWith("ses") || t.EndsWith("xes") ||
                             t.EndsWith("zes") || t.EndsWith("ches") || t.EndsWith("shes")))
            return t[..^2];
        if (t.Length > 1 && t.EndsWith("s") && !t.EndsWith("ss")) return t[..^1];
        return t;
    }

    private static List<string> Tokenize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return [];
        var cleaned = new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        return cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
