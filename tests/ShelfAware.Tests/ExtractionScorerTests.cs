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
