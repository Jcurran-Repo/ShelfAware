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
    /// <summary>The longest a tag may be. ⚠️ A BOUND, not a style preference. Normalizing a candidate
    /// puts it in a Unicode normal form, and NFC's canonical-ordering step is quadratic in the length of
    /// a single run of combining marks — so without a cap, a tag is a free, unauthenticated way to pin a
    /// core for as long as you like. The path has no other brake on it: the tag box carries whatever the
    /// SignalR message size allows (4 MB), <c>FindNearDuplicate</c> runs at stage one of
    /// <c>Upload.AddTag</c> — before the advisor, so before any credit gate or usage cap — and the
    /// column has no length, so one stored monster is re-normalized on every later call for that
    /// household. 64 is past every real tag ("Storage Bags" is 12) and short enough that the quadratic
    /// term cannot matter.</summary>
    public const int MaxLength = 64;

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
        if (candidate.Trim().Length > MaxLength) return null; // not a tag — see MaxLength
        // ⚠️ Measured AFTER trimming, matching Canonicalize. They disagreed for one commit — this one
        // measured the raw string, that one the trimmed — so a 64-character tag with a trailing space was
        // "not a tag" here and a perfectly good tag there. Two arithmetics for one question, inside the
        // file the repo designates as the single home for tag identity.
        var key = Normalize(candidate);
        if (key.Length == 0) return null;

        // ⚠️ TWO passes, because one pass answers the wrong question. Checking both conditions per
        // element means a one-edit neighbour EARLIER in the list beats an identical tag later: with
        // ["Pants", "Pan"] a candidate of "Pans" normalizes to "pan", matches "pant" by one insertion,
        // and the household is offered "Pants" for a tag it already has as "Pan". Harmless while this
        // only ever read text a person typed; it stopped being harmless when the LLM advisor started
        // asking this question about a MODEL's reply, where naming the wrong existing tag is a wrong
        // answer rather than a missed one.
        var keys = new List<(string Tag, string Key)>();
        foreach (var tag in existing)
        {
            // ⚠️ EVERY side, not just the candidate. The first version of this cap guarded the
            // candidate only — and this loop normalizes each EXISTING entry, so one over-long entry in the
            // vocabulary re-paid the quadratic cost on every later call. That was not hypothetical: the
            // cap made FindNearDuplicate answer null for an over-long candidate, Upload.AddTag reads null
            // as "genuinely new", and AddNewTag put the monster straight into the in-memory vocabulary
            // every later lookup then walked. The cap closed the front door and held the back one open.
            //
            // Skipped rather than measured-after-normalizing, and it costs nothing: an entry longer than
            // the cap cannot be within one edit of a candidate that is within it.
            //
            // Stryker disable once Statement: removing this `continue` is UNOBSERVABLE in the result and
            // that is exactly what it is for — it changes what the loop COSTS, not what it answers. An
            // entry past the cap normalizes to something no in-cap candidate can equal or be one edit
            // from, so every assertion reads the same with or without it. What differs is that the
            // mutant re-runs NFC over a run of combining marks whose ordering step is quadratic, on a
            // path that runs before the credit gate. Killing it would need a test that observes wall
            // time, which is a flaky test pinning a real guard — a worse trade than this line.
            //
            // ⚠️ Trimmed, matching the candidate check above and Canonicalize. Those two disagreed for
            // one commit, and writing a third arithmetic here would have been the same defect again.
            if (tag.Trim().Length > MaxLength) continue;
            var other = Normalize(tag);
            if (other == key) return tag;
            keys.Add((tag, other));
        }
        // One-edit typo or a trailing-letter slip on an otherwise-identical tag.
        foreach (var (tag, other) in keys)
            if (Math.Abs(other.Length - key.Length) <= 1 && LevenshteinAtMost1(key, other)) return tag;
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
        if (tag.Length is 0 or > MaxLength) return null; // see MaxLength: a 4 MB "tag" is not a tag
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
    //
    // ⚠️ And put it in one Unicode normal form first, because "Café" typed by the household and
    // "Café" returned by a model can be different strings — a precomposed é against an e plus a
    // combining accent. They are one word to anyone reading them, and an ordinal comparison calls them
    // different, so the dedup declines and the tag cloud fragments on a difference nobody can see. The
    // Levenshtein pass below does not rescue it either: the two spellings are two edits apart, not one.
    private static string Normalize(string s)
    {
        var collapsed = string.Join(' ', Fold(s).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return collapsed.EndsWith('s') && collapsed.Length > 3 ? collapsed[..^1] : collapsed;
    }

    // Ill-formed UTF-16 cannot be normalized and throws. Comparing it as written is the honest fallback:
    // it can only ever fail to find a near-duplicate, never find the wrong one.
    private static string Fold(string s)
    {
        try { return s.Normalize(); }
        catch (ArgumentException) { return s; }
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
