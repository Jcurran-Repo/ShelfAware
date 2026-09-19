using ShelfAware.Core.Domain;
using ShelfAware.Core.Tagging;

namespace ShelfAware.Tests;

public class TagVocabularyTests
{
    private static readonly string[] Existing = ["Condiment", "Canned", "Paper Goods"];

    // The seed vocabulary is public API — these strings are the app's default tags, so pin the exact set.
    [Fact]
    public void The_seed_vocabulary_is_the_expected_set() =>
        Assert.Equal(
            new[]
            {
                "Condiment", "Sauce", "Canned", "Snack", "Spice", "Baking", "Breakfast",
                "Bakery", "Deli", "Frozen Meal", "Protein",
                "Cleaning", "Laundry", "Paper Goods", "Trash Bags", "Storage Bags",
                "First Aid", "Pet Food", "Pet Treats",
            },
            TagVocabulary.Seed);

    // The edit-distance floor: a single insert/delete (length differs by one) or substitution (same
    // length) is a near-dup; two edits apart is not. Pins the `|len diff| <= 1` guard and the Levenshtein
    // insertion/substitution branches (including the length-ordering, which must not depend on argument
    // order — the candidate can be longer OR shorter than the existing tag).
    [Theory]
    [InlineData("Cleanning", "Cleaning")]   // one insertion — candidate LONGER
    [InlineData("Cleaing", "Cleaning")]     // one deletion  — candidate SHORTER
    [InlineData("Sondiment", "Condiment")]  // one substitution — equal length
    public void FindNearDuplicate_catches_a_single_edit(string candidate, string existing) =>
        Assert.Equal(existing, TagVocabulary.FindNearDuplicate(candidate, [existing]));

    [Theory]
    [InlineData("Snap", "Snack")]      // two edits apart (same normalized length)
    [InlineData("Cheddar", "Cheese")]  // unrelated
    public void FindNearDuplicate_rejects_two_edits_apart(string candidate, string existing) =>
        Assert.Null(TagVocabulary.FindNearDuplicate(candidate, [existing]));

    // Normalize drops a trailing plural 's' (length > 3), which is what lets a plural whose SINGULAR is a
    // near-dup match even when the plural itself is two apart: "Boxes" → "boxe" is one edit from "box",
    // but "boxes" is two.
    [Fact]
    public void FindNearDuplicate_matches_a_plural_whose_singular_is_close() =>
        Assert.Equal("Box", TagVocabulary.FindNearDuplicate("Boxes", ["Box"]));

    // ...and the drop is a PLURAL rule, not an every-word rule: lopping the last letter off a non-plural
    // word ("Card" → "car") must not manufacture a match to "Cat".
    [Fact]
    public void FindNearDuplicate_does_not_drop_a_non_plural_final_letter() =>
        Assert.Null(TagVocabulary.FindNearDuplicate("Card", ["Cat"]));

    // The drop needs length > 3 so it never eats a whole short word: a 3-letter "…s" keeps its 's', so
    // "gas" stays "gas" (one edit from "gasp") instead of collapsing to "ga" (two, and no match).
    [Fact]
    public void The_plural_drop_spares_a_three_letter_word() =>
        Assert.Equal("gasp", TagVocabulary.FindNearDuplicate("gas", ["gasp"]));

    // Canonicalize resolves a candidate through the vocabulary: exact match, then a near-dup of a
    // vocabulary tag (so "Condimen" is stored as the canonical "Condiment"), then the candidate itself.
    [Fact]
    public void Canonicalize_snaps_a_near_duplicate_to_the_vocabulary_form() =>
        Assert.Equal("Condiment", TagVocabulary.Canonicalize("Condimen", [], ["Condiment"]));

    [Fact]
    public void Canonicalize_coins_a_genuinely_new_tag_as_itself() =>
        Assert.Equal("Bakery", TagVocabulary.Canonicalize("Bakery", [], ["Condiment"]));

    // An EXACT vocabulary match wins over a near-dup: with a not-fully-deduped vocabulary holding both
    // "Condiments" and "Condiment", the candidate "Condiment" must resolve to itself, not be overwritten
    // by the first near-dup ("Condiments").
    [Fact]
    public void Canonicalize_prefers_an_exact_match_over_a_near_duplicate() =>
        Assert.Equal("Condiment", TagVocabulary.Canonicalize("Condiment", [], ["Condiments", "Condiment"]));

    [Theory]
    [InlineData("condiment")]    // exact (case) already carried
    [InlineData("Condiments")]   // near-dup already carried
    public void Canonicalize_returns_null_when_the_product_already_carries_it(string candidate) =>
        Assert.Null(TagVocabulary.Canonicalize(candidate, ["Condiment"], ["Condiment"]));

    [Fact]
    public void Canonicalize_returns_null_for_blank_input() =>
        Assert.Null(TagVocabulary.Canonicalize("   ", [], []));

    [Fact]
    public void ApplyTags_adds_a_new_tag_and_teaches_the_vocabulary()
    {
        var product = new Product { Name = "Ketchup" };
        var vocab = new List<string>();

        TagVocabulary.ApplyTags(product, ["Condiment"], vocab);

        Assert.Equal(["Condiment"], product.Tags.Select(t => t.Value));
        Assert.Contains("Condiment", vocab); // a newly coined tag is added so later tags dedup against it
    }

    [Fact]
    public void ApplyTags_skips_a_tag_the_product_already_carries()
    {
        var product = new Product { Name = "Ketchup" };
        product.Tags.Add(new ProductTag { Value = "Condiment" });
        var vocab = new List<string> { "Condiment" };

        TagVocabulary.ApplyTags(product, ["condiment", "Condiments"], vocab); // both dup the existing tag

        Assert.Single(product.Tags);
    }

    [Fact]
    public void ApplyTags_dedups_within_one_batch()
    {
        var product = new Product { Name = "Chips" };
        var vocab = new List<string>();

        TagVocabulary.ApplyTags(product, ["Snack", "Snacks", "snack"], vocab); // three spellings of one tag

        Assert.Equal(["Snack"], product.Tags.Select(t => t.Value));
    }

    [Theory]
    [InlineData("condiment")]      // casing
    [InlineData("Condiments")]     // plural
    [InlineData("  Condiment  ")]  // whitespace
    [InlineData("Sondiment")]      // one-edit typo (single substitution)
    public void FindNearDuplicate_CatchesTrivialVariants(string candidate)
    {
        Assert.Equal("Condiment", TagVocabulary.FindNearDuplicate(candidate, Existing));
    }

    [Theory]
    [InlineData("Snack")]
    [InlineData("Soft Drink")]   // a real synonym of nothing here — plain code can't know; that's the LLM's job
    [InlineData("Spice")]
    public void FindNearDuplicate_ReturnsNull_ForGenuinelyNewTags(string candidate)
    {
        Assert.Null(TagVocabulary.FindNearDuplicate(candidate, Existing));
    }

    [Fact]
    public void Two_unicode_spellings_of_one_word_are_one_tag()
    {
        // A precomposed "\u00e9" against an "e" plus a combining accent: one word to anyone reading them,
        // two strings to an ordinal comparison, and TWO edits apart \u2014 so the near-duplicate pass does
        // not rescue it. Normalizing the key is part of what "the same tag" means, and this file owns
        // that question for product tags and recipe tags alike.
        Assert.Equal("Caf\u00e9", TagVocabulary.FindNearDuplicate("Cafe\u0301", ["Caf\u00e9"]));
        Assert.Equal("Cafe\u0301", TagVocabulary.FindNearDuplicate("Caf\u00e9", ["Cafe\u0301"]));
    }

    [Fact]
    public void A_tag_that_cannot_be_normalized_is_compared_as_written_rather_than_throwing()
    {
        // \u26a0\ufe0f Ill-formed UTF-16 (here a lone high surrogate, which is what a truncated emoji leaves
        // behind) cannot be put in a normal form \u2014 string.Normalize throws ArgumentException. The dedup
        // runs on household-typed text and on model replies, so it must degrade rather than throw: the
        // fallback compares the string as written, which can only ever fail to find a near-duplicate and
        // never find the wrong one. Pinned because the branch had no coverage when it was written.
        Assert.Null(TagVocabulary.FindNearDuplicate("Soda\ud83d", ["Cleaning"]));
        Assert.Equal("Soda\ud83d", TagVocabulary.FindNearDuplicate("Soda\ud83d", ["Soda\ud83d"]));
    }

    [Fact]
    public void An_exact_match_beats_a_one_edit_neighbour_that_comes_first()
    {
        // \u26a0\ufe0f The list order must not decide the answer. Checking both conditions in one pass let a
        // one-edit neighbour earlier in the household's tags win over an identical tag later: "Pans"
        // normalizes to "pan", which is one insertion from "pant", so ["Pants", "Pan"] answered "Pants"
        // for a tag the household already had as "Pan". Tolerable while this only read text a person
        // typed; a wrong answer once the LLM advisor started asking it about a model's reply.
        Assert.Equal("Pan", TagVocabulary.FindNearDuplicate("Pans", ["Pants", "Pan"]));
        Assert.Equal("Ice", TagVocabulary.FindNearDuplicate("Ices", ["Rice", "Ice"]));
    }

    [Fact]
    public void A_candidate_longer_than_a_tag_could_be_is_not_a_tag()
    {
        // \u26a0\ufe0f A BOUND, not tidiness. Normalizing runs NFC, whose canonical ordering is quadratic in
        // the length of one run of combining marks \u2014 and this method sits at stage one of Upload.AddTag,
        // before the advisor and so before any credit gate or usage cap, reading a box with no other
        // limit than the 4 MB SignalR message size. Without the cap a tag is a free way to pin a core.
        var monster = "a" + new string('\u0301', TagVocabulary.MaxLength * 2);

        // ⚠️ Against an entry it WOULD otherwise match. The first version of this line compared the
        // monster to "Snack", which never matched with or without the cap — an assertion that passed on
        // the parent and passed with the guard deleted, in the test written to pin the guard.
        Assert.Null(TagVocabulary.FindNearDuplicate(monster, [monster]));
        // And every side, not just the candidate: an over-long entry in the VOCABULARY was re-normalized
        // on every later lookup, which is the cost the cap exists to remove.
        Assert.Null(TagVocabulary.FindNearDuplicate("Snack", [monster]));
        Assert.Null(TagVocabulary.Canonicalize(monster, [], [.. TagVocabulary.Seed]));
        // And the cap does not bite a real tag. A candidate exactly AT the limit is still read, so the
        // boundary is off-by-one-proof in the direction that would silently drop a legitimate tag.
        var atTheLimit = new string('x', TagVocabulary.MaxLength);
        Assert.Equal(atTheLimit, TagVocabulary.FindNearDuplicate(atTheLimit, [atTheLimit]));
        Assert.Equal(atTheLimit, TagVocabulary.Canonicalize(atTheLimit, [], []));
        Assert.All(TagVocabulary.Seed, tag => Assert.True(tag.Length <= TagVocabulary.MaxLength,
            $"Seed tag \"{tag}\" is longer than the cap, so the vocabulary cannot dedup against itself."));
    }
}
