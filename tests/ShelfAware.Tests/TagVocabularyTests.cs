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
}
