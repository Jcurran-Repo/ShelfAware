using ShelfAware.Core.Prediction;

namespace ShelfAware.Tests;

// The one definition of how a prediction is rendered — the chip class, the label, and the two day
// phrasings — shared by the dashboard, the grocery list, the products grid and product detail, which each
// held a byte-identical private copy until 2026-09-19. Each arm is distinct, so a mutated switch fails
// exactly its row.
public class PredictionDisplayTests
{
    [Theory]
    [InlineData(PredictionStatus.Overdue, "overdue", "Overdue")]
    [InlineData(PredictionStatus.DueSoon, "duesoon", "Due soon")]
    [InlineData(PredictionStatus.Stocked, "stocked", "Stocked")]
    [InlineData(PredictionStatus.Unknown, "unknown", "Still learning")]
    public void Each_status_maps_to_one_chip_class_and_label(PredictionStatus status, string chipClass, string label)
    {
        Assert.Equal(chipClass, PredictionDisplay.ChipClass(status));
        Assert.Equal(label, PredictionDisplay.Label(status));
    }

    // A status outside the enum can only arrive from a cast or a tampered value; it must read as the
    // honest "we don't know" rather than throwing inside a render.
    [Fact]
    public void An_unknown_status_value_falls_back_to_still_learning()
    {
        var bogus = (PredictionStatus)99;
        Assert.Equal("unknown", PredictionDisplay.ChipClass(bogus));
        Assert.Equal("Still learning", PredictionDisplay.Label(bogus));
    }

    [Theory]
    [InlineData(-2, "2 days overdue")]
    [InlineData(-1, "1 day overdue")]   // the "1 days over" bug, which shipped twice from two private copies
    [InlineData(0, "Due today")]
    [InlineData(1, "Due in 1 day")]
    [InlineData(3, "Due in 3 days")]
    public void Urgency_is_the_standalone_sentence_form(int days, string expected) =>
        Assert.Equal(expected, PredictionDisplay.Urgency(days));

    [Theory]
    [InlineData(-2, "2 days overdue")]
    [InlineData(-1, "1 day overdue")]
    [InlineData(0, "today")]
    [InlineData(1, "in 1 day")]
    [InlineData(3, "in 3 days")]
    public void Relative_is_the_inline_phrase_form(int days, string expected) =>
        Assert.Equal(expected, PredictionDisplay.Relative(days));

    // The late phrasing is the one that has actually gone wrong, so pin that the two forms share it
    // rather than each spelling it out — the whole reason Overdue() is factored out.
    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(-400)]
    public void Both_forms_word_lateness_identically(int days) =>
        Assert.Equal(PredictionDisplay.Urgency(days), PredictionDisplay.Relative(days));

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "s")]
    [InlineData(2, "s")]
    [InlineData(-1, "s")]   // callers negate before asking, so a negative here is a caller bug, not a singular
    public void Plural_is_empty_only_for_exactly_one(int n, string expected) =>
        Assert.Equal(expected, PredictionDisplay.Plural(n));
}
