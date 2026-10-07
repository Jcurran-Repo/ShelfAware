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

    /// <summary>The most a Unicode canonical composition can shrink text by: NFC only ever merges a
    /// canonical decomposition back into the one character it came from, and no character's canonical
    /// decomposition is longer than four code points (UAX #15; the longest are the Greek extended
    /// letters with three marks, e.g. U+1FAF, and a Hangul syllable is three jamo). So text of N code
    /// points — once its whitespace runs are collapsed, which composition never does — is at least N/4
    /// long in any normal form. ⚠️ That is what lets <see cref="IsOverLengthInAnyForm"/> refuse a stored
    /// monster in LINEAR time, without first paying the quadratic cost it exists to avoid.</summary>
    public const int MaxCanonicalExpansion = 4;

    /// <summary>How many vocabulary entries an advisor prompt may carry — the bound on the one input to a
    /// charged call that the household controls the SIZE of. ⚠️ A plain <c>Take</c> would be the wrong
    /// bound: it keeps whichever tags happen to sort first and silently degrades the synonym check for
    /// exactly the households with the most tags. <see cref="NearestForPrompt(string, IEnumerable{string})"/> keeps the entries
    /// closest to what is being asked about instead, so the tag the model should match is in the prompt
    /// whenever the cheap matcher can see that it is close. 40 is past every real vocabulary (the two
    /// seed lists are 19 and 27) and, at 64 characters a tag, bounds the vocabulary's share of a prompt
    /// to a few hundred tokens.</summary>
    public const int PromptVocabularyLimit = 40;

    /// <summary>Whether this text is too long to be a tag — THE one place that question is answered, and
    /// the only place <see cref="MaxLength"/> may be compared against (held by <c>TagLengthSiteTests</c>).
    /// <para>⚠️ It exists because the same arithmetic was written seven times and one copy was different.
    /// Two sites in this file disagreed for a commit — one measured the raw string, one the trimmed — so a
    /// 64-character tag with a trailing space was "not a tag" here and a good tag there. The commit that
    /// reconciled them then wrote an eighth copy in <c>AnthropicTagAdvisor</c>, untrimmed again, under a
    /// comment in this file saying a third arithmetic would be the same defect. That is the shape
    /// CLAUDE.md's item 41 is on file for: the count only falls when the RULE moves into one place.</para>
    /// <para>⚠️ Trims first. Callers pass raw user input, and the padding is not part of the tag —
    /// <see cref="Canonicalize"/> stores the trimmed form, so measuring the untrimmed one would refuse a
    /// tag the store would have accepted.</para></summary>
    public static bool IsOverLength(string candidate) => candidate.Trim().Length > MaxLength;

    /// <summary>Whether this text is too long to be a tag in ANY form — so far past the cap that no normal
    /// form of it could come back inside, which is the question the stored-form boot pass has to answer
    /// about a legacy row BEFORE it normalizes it. Linear: whitespace runs are collapsed (the one shrink
    /// composition does not make), and the rest is <see cref="MaxCanonicalExpansion"/>. A row this answers
    /// true for is not a tag and never was; a row it answers false for is at most a few hundred characters,
    /// which NFC handles in no time.</summary>
    public static bool IsOverLengthInAnyForm(string candidate) =>
        CollapseWhitespace(candidate).Length > MaxLength * MaxCanonicalExpansion;

    /// <summary>What to tell someone who typed one, so three screens can't word the same refusal three
    /// ways. ⚠️ "or fewer", not "under": <see cref="IsOverLength"/> admits a tag of exactly
    /// <see cref="MaxLength"/>, and the copy this replaced said "under 64 characters" on both screens
    /// that carried it — a message that contradicted the guard it described.</summary>
    public static string TooLongMessage => $"That tag is too long \u2014 keep it to {MaxLength} characters or fewer.";

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
        var trimmed = candidate.Trim();
        if (IsOverLength(trimmed)) return null; // not a tag — see IsOverLength
        // ⚠️ Keys the string the cap MEASURED, not the one the caller passed. Those were different for
        // one commit: the cap read Trim().Length while the key was taken from the raw argument, so four
        // megabytes of padding around sixty real characters passed the cap and was then NFC-normalized,
        // split and re-joined at full length — the allocation the cap exists to prevent, waved through by
        // the cap itself. One string, measured and used.
        var key = MatchKey(trimmed);
        if (key.Length == 0) return null;

        // ⚠️ TWO passes, because one pass answers the wrong question. Checking both conditions per
        // element means a one-edit neighbour EARLIER in the list beats an identical tag later: with
        // ["Pants", "Pan"] a candidate of "Pans" keys to "pan", matches "pant" by one insertion, and the
        // household is offered "Pants" for a tag it already has as "Pan". Harmless while this only ever
        // read text a person typed; it stopped being harmless when the LLM advisor started asking this
        // question about a MODEL's reply, where naming the wrong existing tag is a wrong answer rather
        // than a missed one.
        var keys = new List<(string Tag, string Key)>();
        foreach (var tag in existing)
        {
            // ⚠️ EVERY side, not just the candidate. The first version of this cap guarded the
            // candidate only — and this loop keys each EXISTING entry, so one over-long entry in the
            // vocabulary re-paid the quadratic cost on every later call. That was not hypothetical: the
            // cap made FindNearDuplicate answer null for an over-long candidate, Upload.AddTag reads null
            // as "genuinely new", and AddNewTag put the monster straight into the in-memory vocabulary
            // every later lookup then walked. The cap closed the front door and held the back one open.
            //
            // ⚠️ This skip CHANGES THE ANSWER, and it is a deliberate trade rather than a free one. The
            // suppression that used to sit here claimed the opposite — "an entry longer than the cap
            // cannot be within one edit of a candidate that is within it", so removing the `continue` was
            // said to be unobservable. That is false, because the key SHRINKS: it collapses interior
            // whitespace runs and NFC composes a base plus a combining mark into one character. An entry
            // of "a", a thousand spaces, "b" is 1002 raw characters and keys to "a b" — a candidate of
            // "a b" matches it exactly, and this line is what makes the method answer "genuinely new"
            // instead. A missing test was presented as an equivalent mutant; the test that disproves it
            // is An_entry_whose_raw_form_is_over_the_cap_is_not_a_dedup_target.
            //
            // The trade is taken anyway, and measured RAW on purpose: the cost this bounds is the cost of
            // normalizing at all, so a cap that had to normalize first to decide would have already paid
            // it. A stored tag can only get here through Canonicalize, which writes the StoredForm — the
            // composed, collapsed text whose raw length IS its keyed length — so a tag written today is
            // never skipped. What this protects against is a legacy row written before the cap existed,
            // where the column has no length and one 4 MB monster would otherwise be re-normalized on
            // every later call for that household. A legacy row written in decomposed form (33 decomposed
            // "é" is 66 raw characters, 33 after NFC) used to drop out of dedup here too; the stored-form
            // boot pass (TagStoredFormMigration) rewrites those rows once, so the only rows this still
            // skips are the ones IsOverLengthInAnyForm says were never tags.
            //
            // ⚠️ The shared predicate, like every other site — see IsOverLength for why writing the
            // arithmetic out here a third time was the defect this file kept reintroducing.
            var entry = tag.Trim();
            if (IsOverLength(entry)) continue;
            var other = MatchKey(entry);
            if (other == key) return tag;
            keys.Add((tag, other));
        }
        // One-edit typo or a trailing-letter slip on an otherwise-identical tag.
        foreach (var (tag, other) in keys)
            if (EditDistance(key, other) <= 1) return tag;
        return null;
    }

    /// <summary>
    /// The <see cref="PromptVocabularyLimit"/> entries of <paramref name="existing"/> closest to what is
    /// being asked about, nearest first — what an advisor puts in its prompt instead of the whole
    /// vocabulary. "Closest" is the cheap matcher's own notion of distance: the edit distance between
    /// <see cref="MatchKey"/>s, so the ranking cannot disagree with <see cref="FindNearDuplicate"/> about
    /// which tags are near (its one-edit neighbours are exactly the entries at distance one here). An
    /// entry's distance to several probes is its distance to the nearest of them, so the recipe advisor
    /// can rank its vocabulary against a recipe's name and every ingredient at once.
    /// <para>Ties keep their input order, so a vocabulary under the limit comes back whole and in a
    /// stable order, and entries past the tag cap are left out for the same reason and at the same
    /// predicate as <see cref="FindNearDuplicate"/> skips them. A probe longer than a tag could be is
    /// read to the cap rather than skipped — a recipe's name is a probe, and its first sixty-four
    /// characters are most of its signal — which also bounds the key's cost on a probe nobody capped.</para>
    /// </summary>
    public static IReadOnlyList<string> NearestForPrompt(IReadOnlyList<string> probes, IEnumerable<string> existing)
    {
        var probeKeys = probes
            .Select(p => MatchKey(p[..Math.Min(p.Length, MaxLength)]))
            .Where(k => k.Length > 0)
            .ToList();
        var ranked = new List<(int Distance, int Order, string Tag)>();
        foreach (var tag in existing)
        {
            if (IsOverLength(tag)) continue; // not a tag — see FindNearDuplicate for the cost this bounds
            var key = MatchKey(tag);
            var distance = probeKeys.Count == 0 ? 0 : probeKeys.Min(p => EditDistance(key, p));
            ranked.Add((distance, ranked.Count, tag));
        }
        return ranked
            .OrderBy(r => r.Distance).ThenBy(r => r.Order)
            .Take(PromptVocabularyLimit)
            .Select(r => r.Tag)
            .ToList();
    }

    /// <summary>The single-probe form of <see cref="NearestForPrompt(IReadOnlyList{string}, IEnumerable{string})"/>:
    /// the entries closest to one candidate tag.</summary>
    public static IReadOnlyList<string> NearestForPrompt(string candidate, IEnumerable<string> existing) =>
        NearestForPrompt([candidate], existing);

    /// <summary>
    /// The canonical form to store a candidate tag as — exact vocabulary match → near-duplicate → the
    /// candidate's own <see cref="StoredForm"/> — or null when <paramref name="existing"/> already carries
    /// that tag (or a near-duplicate of it) and it should be skipped. THE one place the
    /// dedup/canonicalization policy lives, so product tags (<see cref="ApplyTags"/>) and recipe tags
    /// (<c>RecipeTagVocabulary</c>) can't drift on what counts as "the same tag".
    /// </summary>
    public static string? Canonicalize(string candidate, IReadOnlyList<string> existing, List<string> vocabulary)
    {
        var tag = candidate.Trim();
        if (tag.Length == 0 || IsOverLength(tag)) return null; // see IsOverLength: a 4 MB "tag" is not a tag
        // ⚠️ AFTER the cap, never before: the stored form is NFC, and the cap is what makes NFC cheap.
        tag = StoredForm(tag);
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

    /// <summary>
    /// The form a tag is STORED in: trimmed, interior whitespace runs collapsed to one space, and in one
    /// Unicode normal form (NFC). Casing is kept — this is what the household sees, not the key it is
    /// compared by. ⚠️ Its raw length is its keyed length, which is the property the dedup's cap relies
    /// on: <see cref="FindNearDuplicate"/> measures an entry raw before paying to key it, so a stored tag
    /// that was longer raw than keyed — decomposed accents, a run of spaces — was inside the cap to the
    /// key and over it to the measure, and quietly dropped out of dedup. <see cref="Canonicalize"/>
    /// writes this form, and the stored-form boot pass rewrites rows written before it did.
    /// </summary>
    public static string StoredForm(string tag) => Fold(CollapseWhitespace(tag));

    /// <summary>
    /// THE key two tags are compared by — the one definition of "the same tag" for the near-duplicate
    /// check, the canonicalization policy, and the advisor's ranking alike: the <see cref="StoredForm"/>,
    /// with script-confusable letters folded to their Latin twins, lowercased, and a trailing plural
    /// 's' dropped so "Condiments" ≈ "condiment".
    /// <para>⚠️ One Unicode normal form first, because "Café" typed by the household and "Café" returned
    /// by a model can be different strings — a precomposed é against an e plus a combining accent. They
    /// are one word to anyone reading them, and an ordinal comparison calls them different, so the dedup
    /// declines and the tag cloud fragments on a difference nobody can see. The edit-distance pass does
    /// not rescue it either: the two spellings are two edits apart, not one.</para>
    /// <para>⚠️ The plural drop is a one-shot rule, not a fixed point: "glass" keys to "glas", and a key
    /// is never re-keyed anywhere. Everything before it IS idempotent, so the key of a key is the key for
    /// any tag that does not end in a plural s.</para>
    /// </summary>
    public static string MatchKey(string tag)
    {
        var key = Skeleton(StoredForm(tag)).ToLowerInvariant();
        return key.EndsWith('s') && key.Length > 3 ? key[..^1] : key;
    }

    private static string CollapseWhitespace(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Ill-formed UTF-16 cannot be normalized and throws. Comparing it as written is the honest fallback:
    // it can only ever fail to find a near-duplicate, never find the wrong one.
    private static string Fold(string s)
    {
        try { return s.Normalize(); }
        catch (ArgumentException) { return s; }
    }

    /// <summary>
    /// The script-confusable fold: letters from other scripts that are drawn identically to a Latin
    /// letter, mapped to that Latin letter. The idea is Unicode TR39's confusable "skeleton" — compare
    /// skeletons, not strings — but this is the COMMON SUBSET a grocery tag could plausibly carry
    /// (Cyrillic and Greek capitals and lowercase that share a glyph with Latin), not the standard's
    /// full table, which runs to thousands of pairs across every script and is the wrong tool for a tag
    /// cloud: a near-duplicate check that folds Cherokee into Latin is not more correct, only slower.
    /// ⚠️ Without it a Cyrillic "Ѕoda" is a brand-new tag beside "Soda", and on the recipe path that
    /// coinage is a silent write.
    /// </summary>
    private static readonly Dictionary<char, char> Confusables = new()
    {
        // Cyrillic — lowercase, then the capitals that look like Latin capitals.
        ['\u0430'] = 'a', ['\u0435'] = 'e', ['\u043E'] = 'o', ['\u0440'] = 'p', ['\u0441'] = 'c',
        ['\u0443'] = 'y', ['\u0445'] = 'x', ['\u0456'] = 'i', ['\u0458'] = 'j', ['\u0455'] = 's',
        ['\u0501'] = 'd', ['\u04BB'] = 'h', ['\u04CF'] = 'l', ['\u051B'] = 'q', ['\u051D'] = 'w',
        ['\u0410'] = 'A', ['\u0415'] = 'E', ['\u041E'] = 'O', ['\u0420'] = 'P', ['\u0421'] = 'C',
        ['\u0423'] = 'Y', ['\u0425'] = 'X', ['\u0406'] = 'I', ['\u0408'] = 'J', ['\u0405'] = 'S',
        ['\u0500'] = 'D', ['\u04BA'] = 'H', ['\u04C0'] = 'I', ['\u051A'] = 'Q', ['\u051C'] = 'W',
        // Armenian vo, and the one Latin-internal twin (IPA script g) that TR39 folds too.
        ['\u0578'] = 'n', ['\u0261'] = 'g',
        // Greek — the capitals that are drawn as Latin capitals, and the two lowercase that are.
        ['\u0391'] = 'A', ['\u0392'] = 'B', ['\u0395'] = 'E', ['\u0396'] = 'Z', ['\u0397'] = 'H',
        ['\u0399'] = 'I', ['\u039A'] = 'K', ['\u039C'] = 'M', ['\u039D'] = 'N', ['\u039F'] = 'O',
        ['\u03A1'] = 'P', ['\u03A4'] = 'T', ['\u03A5'] = 'Y', ['\u03A7'] = 'X',
        ['\u03BF'] = 'o', ['\u03BD'] = 'v',
    };

    // Fold confusables ONLY in text that is otherwise Latin: every letter that is not itself a
    // confusable has to sit in a Latin block, so a genuinely Cyrillic or Greek tag ("Сода", "Νερό") keeps
    // its own letters and is never mistaken for a Latin one. A string made entirely of confusables has
    // no other letters to object, and folds — it reads as the Latin word to anyone looking at it.
    private static string Skeleton(string s)
    {
        var mixed = false;
        foreach (var c in s)
        {
            if (Confusables.ContainsKey(c)) mixed = true;
            else if (char.IsLetter(c) && !IsLatinBlock(c)) return s;
        }
        return mixed
            ? string.Create(s.Length, s, static (span, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                    span[i] = Confusables.TryGetValue(source[i], out var latin) ? latin : source[i];
            })
            : s;
    }

    // The Latin blocks a tag could plausibly use: Basic Latin through IPA Extensions (one contiguous run
    // from U+0041 — the non-letters inside it, digits and punctuation, are excluded by the caller's
    // IsLetter), and Latin Extended Additional (the Vietnamese letters). .NET has no script property to
    // ask, so the blocks are the honest answer; a letter outside them is read as another script.
    private static bool IsLatinBlock(char c) => c <= '\u02AF' || c is >= '\u1E00' and <= '\u1EFF';

    // The number of single-character edits (insert / delete / substitute) that turn a into b — the one
    // edit distance, used both as the near-duplicate predicate (at most one) and as the ranking the
    // advisor prompt is trimmed by. The classic two-row dynamic programme; both inputs are keys, so both
    // are bounded by the tag cap and the table is at most 65 wide.
    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitute = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                var delete = previous[j] + 1;
                var insert = current[j - 1] + 1;
                current[j] = Math.Min(substitute, Math.Min(delete, insert));
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
