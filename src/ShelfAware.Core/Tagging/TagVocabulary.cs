using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Tagging;

/// <summary>
/// The curated starter tag vocabulary plus the plain-code near-duplicate check that guards the tag cloud
/// from fragmenting ("Condiment" vs "Condiments" vs "condiment"). Pure C#, unit-tested. A new tag that
/// passes this check can still be escalated to an <see cref="ITagAdvisor"/> for a semantic look
/// (catches synonyms with no shared letters, e.g. "Soda" ≈ "Soft Drink").
/// </summary>
public static class TagVocabulary
{
    /// <summary>Starter tags. Descriptive, orthogonal to the store-aisle Category; users can add more.</summary>
    public static readonly IReadOnlyList<string> Seed =
    [
        "Condiment", "Sauce", "Canned", "Snack", "Spice", "Baking", "Breakfast",
        "Bakery", "Deli", "Frozen Meal", "Protein",
        "Cleaning", "Laundry", "Paper Goods", "Trash Bags", "Storage Bags",
        "First Aid", "Pet Food", "Pet Treats",
    ];

    /// <summary>Returns an existing tag the candidate is a near-duplicate of (case/whitespace/simple
    /// plural/typo), or null if it's genuinely new. Cheap and instant — the first dedup stage.</summary>
    public static string? FindNearDuplicate(string candidate, IEnumerable<string> existing)
    {
        var key = Normalize(candidate);
        if (key.Length == 0) return null;
        foreach (var tag in existing)
        {
            var other = Normalize(tag);
            if (other == key) return tag;
            // One-edit typo or a trailing-letter slip on an otherwise-identical tag.
            if (Math.Abs(other.Length - key.Length) <= 1 && LevenshteinAtMost1(key, other)) return tag;
        }
        return null;
    }

    /// <summary>
    /// Canonicalize each tag against the vocabulary (exact match → near-duplicate → genuinely new) and
    /// apply it to the product unless it already carries the tag or a near-duplicate of it; newly coined
    /// tags are added to <paramref name="vocabulary"/> so later tags in the same batch dedup against them.
    /// The ONE tag-apply path — shared by receipt confirmation and the chat/voice tools so they can't
    /// drift on dedup policy.
    /// </summary>
    public static void ApplyTags(Product product, IReadOnlyList<string> tags, List<string> vocabulary)
    {
        foreach (var raw in tags)
        {
            var tag = raw.Trim();
            if (tag.Length == 0) continue;
            var canonical = vocabulary.FirstOrDefault(v => string.Equals(v, tag, StringComparison.OrdinalIgnoreCase))
                ?? FindNearDuplicate(tag, vocabulary)
                ?? tag;
            var existing = product.Tags.Select(t => t.Value).ToList();
            if (existing.Any(v => string.Equals(v, canonical, StringComparison.OrdinalIgnoreCase))) continue;
            if (FindNearDuplicate(canonical, existing) is not null) continue;
            product.Tags.Add(new ProductTag { Value = canonical });
            if (!vocabulary.Any(v => string.Equals(v, canonical, StringComparison.OrdinalIgnoreCase)))
                vocabulary.Add(canonical);
        }
    }

    // Lowercase, collapse whitespace, drop a trailing plural 's' so "Condiments" ≈ "condiment".
    private static string Normalize(string s)
    {
        var collapsed = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return collapsed.EndsWith('s') && collapsed.Length > 3 ? collapsed[..^1] : collapsed;
    }

    // True when a and b differ by at most one single-character edit (insert/delete/substitute).
    private static bool LevenshteinAtMost1(string a, string b)
    {
        if (a == b) return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (longer.Length - shorter.Length > 1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < shorter.Length && j < longer.Length)
        {
            if (shorter[i] == longer[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (shorter.Length == longer.Length) { i++; j++; }   // substitution
            else j++;                                            // insertion in longer
        }
        return true;
    }
}
