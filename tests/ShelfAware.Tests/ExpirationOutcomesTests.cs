using ShelfAware.Core.Domain;
using ShelfAware.Core.Reporting;

namespace ShelfAware.Tests;

public class ExpirationOutcomesTests
{
    private static readonly DateOnly Today = new(2026, 7, 18);

    private static LabeledPurchase Milk(int boughtDay, int labelDay) =>
        new(1, "Whole Milk", new DateOnly(2026, 7, boughtDay), new DateOnly(2026, 7, labelDay), 3.50m);

    private static LabelOutcome Judge(
        LabeledPurchase p, DateOnly[]? purchases = null, (DateOnly, SignalKind)[]? signals = null) =>
        ExpirationOutcomes.Judge(p, purchases ?? [], signals ?? [], Today);

    [Fact]
    public void The_label_day_itself_is_still_good()
    {
        // v3.6's rule: "best by" the 18th, judged on the 18th, is not yet passed.
        Assert.Equal(LabelOutcome.StillAhead, Judge(Milk(boughtDay: 1, labelDay: 18)));
        Assert.Equal(LabelOutcome.PassedQuietly, Judge(Milk(boughtDay: 1, labelDay: 17)));
    }

    [Fact]
    public void Rebuying_before_the_label_supersedes()
    {
        Assert.Equal(LabelOutcome.Superseded,
            Judge(Milk(boughtDay: 1, labelDay: 10), purchases: [new DateOnly(2026, 7, 8)]));
        // A rebuy AFTER the label is a fresh jug, not evidence about the dead one.
        Assert.Equal(LabelOutcome.PassedQuietly,
            Judge(Milk(boughtDay: 1, labelDay: 10), purchases: [new DateOnly(2026, 7, 12)]));
    }

    [Fact]
    public void An_OutNow_before_the_label_means_it_was_finished()
    {
        Assert.Equal(LabelOutcome.MarkedOut,
            Judge(Milk(boughtDay: 1, labelDay: 10), signals: [(new DateOnly(2026, 7, 9), SignalKind.OutNow)]));
    }

    [Fact]
    public void Only_a_restock_after_the_label_is_an_override()
    {
        // The freezer override — mirrors the predictor's live rule exactly.
        Assert.Equal(LabelOutcome.Overridden,
            Judge(Milk(boughtDay: 1, labelDay: 10), signals: [(new DateOnly(2026, 7, 12), SignalKind.Restocked)]));
        // Restocked on/before the label is a casual "I have it" — it must not resolve anything.
        Assert.Equal(LabelOutcome.PassedQuietly,
            Judge(Milk(boughtDay: 1, labelDay: 10), signals: [(new DateOnly(2026, 7, 10), SignalKind.Restocked)]));
    }

    [Fact]
    public void Consumption_evidence_beats_the_freezer_override()
    {
        // Marked out before the label AND restocked after: it was finished; the later restock is a
        // new state, not a rescue of the judged jug.
        Assert.Equal(LabelOutcome.MarkedOut,
            Judge(Milk(boughtDay: 1, labelDay: 10),
                signals: [(new DateOnly(2026, 7, 5), SignalKind.OutNow), (new DateOnly(2026, 7, 12), SignalKind.Restocked)]));
    }
}
