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
    /// The canonical form to store a candidate tag as — exact vocabulary match → near-duplicate → the
    /// candidate itself — or null when <paramref name="existing"/> already carries that tag (or a
    /// near-duplicate of it) and it should be skipped. THE one place the dedup/canonicalization policy
    /// lives, so product tags (<see cref="ApplyTags"/>) and recipe tags (<c>RecipeTagVocabulary</c>)
    /// can't drift on what counts as "the same tag".
    /// </summary>
    public static string? Canonicalize(string candidate, IReadOnlyList<string> existing, List<string> vocabulary)
    {
        var tag = candidate.Trim();
        if (tag.Length == 0) return null;
        // Resolve against the vocabulary in order: an exact (case-insensitive) match, then a near-dup of
        // a known tag, then the candidate itself when it is genuinely new.
        var canonical = vocabulary.FirstOrDefault(v => string.Equals(v, tag, StringComparison.OrdinalIgnoreCase));
        canonical ??= FindNearDuplicate(tag, vocabulary);
        canonical ??= tag;
        if (existing.Any(v => string.Equals(v, canonical, StringComparison.OrdinalIgnoreCase))) return null;
        if (FindNearDuplicate(canonical, existing) is not null) return null;
        return canonical;
    }

    /// <summary>
    /// Canonicalize each tag (<see cref="Canonicalize"/>) and apply it to the product unless it already
    /// carries the tag or a near-duplicate; newly coined tags are added to <paramref name="vocabulary"/>
    /// so later tags in the same batch dedup against them. The ONE product-tag-apply path — shared by
    /// receipt confirmation and the chat/voice tools so they can't drift on dedup policy.
    /// </summary>
    public static void ApplyTags(Product product, IReadOnlyList<string> tags, List<string> vocabulary)
    {
        foreach (var raw in tags)
        {
            var canonical = Canonicalize(raw, product.Tags.Select(t => t.Value).ToList(), vocabulary);
            if (canonical is null) continue;
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

    // True when a and b differ by at most one single-character edit (insert/delete/substitute). The one
    // caller (FindNearDuplicate) has already returned on an exact match, so a and b are never equal here;
    // the loop handles a == b correctly anyway (zero edits), so no separate base case is needed.
    private static bool LevenshteinAtMost1(string a, string b)
    {
        // Stryker disable once Equality: `<=` → `<` is unobservable — at equal lengths either assignment
        // gives a valid (shorter, longer) pair and the loop is symmetric, so the result is unchanged. The
        // category also suppresses `>`, which IS killable (it puts the longer string in `shorter`, breaking
        // the insertion branch) — pinned by the shorter-candidate case in FindNearDuplicate_catches_a_single_edit.
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        // Stryker disable once Boolean: this `return false` is unreachable — the sole caller only invokes us
        // with |a.Length - b.Length| <= 1, so `> 1` is never true. Kept as defense-in-depth for the loop
        // below, which assumes the length gap is at most one.
        if (longer.Length - shorter.Length > 1) return false;
        int i = 0, j = 0, edits = 0;
        // Only `i` needs bounding: with the length gap capped at one (guaranteed above), `j` runs at most
        // one ahead of `i`, so `j` reaches `longer.Length` exactly as `i` reaches `shorter.Length` and the
        // loop has already exited — a `j < longer.Length` guard here would be redundant (and untestable).
        while (i < shorter.Length)
        {
            if (shorter[i] == longer[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (shorter.Length == longer.Length) { i++; j++; }   // substitution
            else j++;                                            // insertion in longer
        }
        return true;
    }
}
