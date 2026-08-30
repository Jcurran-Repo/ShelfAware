using ShelfAware.Core.Domain;
using ShelfAware.Core.Evaluation;
using ShelfAware.Core.Extraction;

namespace ShelfAware.Tests;

public class ExtractionScorerTests
{
    private static ExpectedLine E(string name, decimal qty = 1, string category = "Produce") =>
        new() { NormalizedName = name, Quantity = qty, Category = category };

    private static ExtractedLine F(string name, decimal qty = 1, Category category = Category.Produce) =>
        new() { RawText = name, NormalizedName = name, Quantity = qty, Category = category };

    [Fact]
    public void Containment_matches_a_concise_label_to_a_verbose_extraction()
    {
        // The 58%→99% lesson: "Lean Ground Beef" IS "All Natural 93% Lean Ground Beef".
        var detail = ExtractionScorer.Score(
            [E("Lean Ground Beef", category: "Meat")],
            [F("All Natural 93% Lean Ground Beef", category: Category.Meat)]);

        Assert.Equal(1, detail.Matched);
        Assert.Equal(1, detail.FieldHits);
        Assert.Empty(detail.MissingExpected);
        Assert.Empty(detail.Unexpected);
    }

    [Fact]
    public void Bare_plural_differences_still_match()
    {
        // Extraction wobbles between "Lime" and "Limes" run to run — that's not a missed line.
        var detail = ExtractionScorer.Score([E("Lime", qty: 4)], [F("Limes", qty: 4)]);

        Assert.Equal(1, detail.Matched);
        Assert.Equal(1, detail.FieldHits);
    }

    [Fact]
    public void Duplicate_expected_lines_each_consume_a_distinct_found_line()
    {
        // A real receipt can list one item twice (the 5/22 eggplant); two expected lines must not
        // both claim the same found line.
        var detail = ExtractionScorer.Score(
            [E("Purple Eggplant", qty: 2), E("Purple Eggplant", qty: 2)],
            [F("Purple Eggplant", qty: 2), F("Purple Eggplant", qty: 2)]);

        Assert.Equal(2, detail.Matched);
        Assert.Empty(detail.Unexpected);
    }

    [Fact]
    public void Wrong_quantity_or_category_matches_the_line_but_misses_the_field()
    {
        var detail = ExtractionScorer.Score(
            [E("Whole Milk", qty: 1, category: "Dairy"), E("Bananas", qty: 2)],
            [F("Whole Milk", qty: 3, category: Category.Dairy), F("Bananas", qty: 2, category: Category.Pantry)]);

        Assert.Equal(2, detail.Matched);
        Assert.Equal(0, detail.FieldHits); // one qty miss, one category miss
        Assert.Contains(detail.Pairs, p => p.Contains("[qty]"));
        Assert.Contains(detail.Pairs, p => p.Contains("[cat"));
    }

    [Fact]
    public void Unrelated_names_do_not_match_and_land_on_both_miss_lists()
    {
        var detail = ExtractionScorer.Score([E("Tomato Paste")], [F("Dish Soap", category: Category.Household)]);

        Assert.Equal(0, detail.Matched);
        Assert.Equal(["Tomato Paste"], detail.MissingExpected);
        Assert.Equal(["Dish Soap"], detail.Unexpected);
    }

    [Fact]
    public void Expected_line_defaults_are_a_blank_name_and_the_other_category()
    {
        var e = new ExpectedLine();
        Assert.Equal("", e.NormalizedName);
        Assert.Equal("Other", e.Category);
    }

    [Fact]
    public void A_tie_in_similarity_takes_the_first_found_line()
    {
        // Two found lines match equally well; the greedy matcher takes the FIRST, so the first line's
        // quantity is the one scored (not the second's).
        var detail = ExtractionScorer.Score(
            [E("Milk", qty: 1, category: "Dairy")],
            [F("Milk", qty: 1, category: Category.Dairy), F("Milk", qty: 2, category: Category.Dairy)]);

        Assert.Equal(1, detail.Matched);
        Assert.Equal(1, detail.FieldHits); // the first (qty 1) matched — a last-wins tie-break would miss
    }

    [Fact]
    public void A_sub_threshold_overlap_is_a_miss_not_a_match()
    {
        // "Whole Milk" vs "Milk Chocolate Bar" share only "milk": containment 1/2 = 0.5, below 0.6.
        var detail = ExtractionScorer.Score([E("Whole Milk")], [F("Milk Chocolate Bar")]);

        Assert.Equal(0, detail.Matched);
        Assert.Equal(["Whole Milk"], detail.MissingExpected);
    }

    [Fact]
    public void Exactly_the_threshold_matches()
    {
        // Containment exactly 0.6 (3 shared of min 5) must match — the threshold is inclusive.
        var detail = ExtractionScorer.Score(
            [E("All Natural Lean Ground Beef", category: "Meat")],
            [F("Lean Ground Beef Value Pack", category: Category.Meat)]);

        Assert.Equal(1, detail.Matched); // lean, ground, beef shared of 5 tokens each = 0.6
    }

    [Fact]
    public void A_quantity_within_the_hundredth_tolerance_is_a_field_hit()
    {
        // The quantity tolerance is <= 0.01 (weight rounding), inclusive at the boundary.
        var detail = ExtractionScorer.Score(
            [E("Ground Beef", qty: 1.00m, category: "Meat")],
            [F("Ground Beef", qty: 1.01m, category: Category.Meat)]);

        Assert.Equal(1, detail.FieldHits);
    }

    [Fact]
    public void A_fully_correct_match_carries_no_field_flags()
    {
        var detail = ExtractionScorer.Score(
            [E("Whole Milk", qty: 1, category: "Dairy")],
            [F("Whole Milk", qty: 1, category: Category.Dairy)]);

        Assert.Equal(1, detail.FieldHits);
        Assert.DoesNotContain("[qty]", detail.Pairs[0]);
        Assert.DoesNotContain("[cat", detail.Pairs[0]);
        // The diagnostic pair reads "<expected>  ↔  <found>" with no stray prefix/suffix — pins both ends.
        Assert.StartsWith("Whole Milk", detail.Pairs[0]);
        Assert.EndsWith("Whole Milk", detail.Pairs[0]);
    }

    [Theory]
    // expected, found, matched, hits → recall, precision, fieldAccuracy — the empty-denominator corners.
    [InlineData(0, 0, 0, 0, 1.0, 1.0, 0.0)]  // nothing expected AND nothing found: vacuously perfect recall+precision
    [InlineData(0, 2, 0, 0, 1.0, 0.0, 0.0)]  // nothing expected but things found: precision 0
    [InlineData(2, 0, 0, 0, 0.0, 0.0, 0.0)]  // things expected but nothing found: both 0
    [InlineData(2, 2, 0, 0, 0.0, 0.0, 0.0)]  // matched 0: field accuracy 0, not a divide-by-zero
    public void Fixture_score_handles_the_empty_denominators(
        int exp, int found, int matched, int hits, double recall, double precision, double field)
    {
        var s = ExtractionScorer.ToFixtureScore("x", exp, found, new ScoreDetail(matched, hits, [], [], []));

        Assert.Equal(recall, s.Recall);
        Assert.Equal(precision, s.Precision);
        Assert.Equal(field, s.FieldAccuracy);
    }

    [Fact]
    public void Aggregate_of_no_scorable_fixtures_is_zero()
    {
        var agg = ExtractionScorer.Aggregate([new FixtureScore { Name = "e", Error = "boom" }]);

        Assert.Equal(0, agg.Recall);
        Assert.Equal(0, agg.Precision);
        Assert.Equal(0, agg.FieldAccuracy);
    }

    [Fact]
    public void Aggregate_takes_the_mean_of_the_scorable_fixtures_not_the_minimum()
    {
        var a = ExtractionScorer.ToFixtureScore("a", 2, 2, new ScoreDetail(2, 2, [], [], [])); // recall/precision/field = 1
        var b = ExtractionScorer.ToFixtureScore("b", 2, 2, new ScoreDetail(1, 0, [], [], [])); // recall/precision 0.5, field 0

        var agg = ExtractionScorer.Aggregate([a, b]);

        Assert.Equal(0.75, agg.Recall);       // mean(1, 0.5), not min 0.5
        Assert.Equal(0.75, agg.Precision);
        Assert.Equal(0.5, agg.FieldAccuracy); // mean(1, 0), not min 0
    }

    [Fact]
    public void An_empty_name_has_zero_similarity_to_anything()
    {
        // An empty token set short-circuits to 0 — otherwise the containment would divide by min(0, n) = 0.
        Assert.Equal(0, ExtractionScorer.TokenSimilarity("", "Whole Milk"));
        Assert.Equal(0, ExtractionScorer.TokenSimilarity("Whole Milk", ""));
    }

    [Fact]
    public void Punctuation_is_a_separator_before_tokenizing()
    {
        // "Ground-Beef," and "Ground Beef" tokenize identically — non-alphanumerics become separators.
        var detail = ExtractionScorer.Score(
            [E("Ground Beef", category: "Meat")],
            [F("Ground-Beef,", category: Category.Meat)]);

        Assert.Equal(1, detail.Matched);
    }

    [Fact]
    public void A_four_letter_plural_still_folds()
    {
        // The plural fold applies from length 4 inclusive: "Cans" -> "can" matches "Can".
        var detail = ExtractionScorer.Score(
            [E("Can", category: "Pantry")],
            [F("Cans", category: Category.Pantry)]);

        Assert.Equal(1, detail.Matched);
    }

    [Fact]
    public void Fixture_score_and_aggregate_compute_the_published_ratios()
    {
        // 3 expected, 4 found, 2 matched, 1 field hit → recall 2/3, precision 2/4, field 1/2.
        var score = ExtractionScorer.ToFixtureScore("r1", expectedCount: 3, foundCount: 4,
            new ScoreDetail(Matched: 2, FieldHits: 1, [], [], []));

        Assert.Equal(2.0 / 3.0, score.Recall, precision: 10);
        Assert.Equal(0.5, score.Precision);
        Assert.Equal(0.5, score.FieldAccuracy);

        // Errored fixtures (missing image, failed read) don't drag the aggregate to zero.
        var aggregate = ExtractionScorer.Aggregate([score, new FixtureScore { Name = "r2", Error = "boom" }]);
        Assert.Equal(2.0 / 3.0, aggregate.Recall, precision: 10);
    }
}
