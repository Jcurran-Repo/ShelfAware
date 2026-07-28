using ShelfAware.Core.Reporting;

namespace ShelfAware.Tests;

public class BacklogSignalsTests
{
    private static readonly DateOnly Today = new(2026, 7, 28);

    /// <summary>Day N of the history, counting from 1 June — so the whole story sits before "today"
    /// and the numbers in the tests read as a calendar rather than as arithmetic.</summary>
    private static DateOnly Day(int day) => new DateOnly(2026, 6, 1).AddDays(day - 1);

    /// <summary>The default item: bought on days 1, 14 and 28 (so a ~13-day rhythm), last bought 30 days
    /// before "today", and therefore well past due — the shape every condition is varied against.</summary>
    private static BacklogInput Item(
        int id = 1,
        string name = "Black Beans",
        DateOnly[]? buys = null,
        DateOnly[]? outages = null,
        decimal quantity = 3,
        decimal spend = 30m,
        int unpriced = 0,
        double? rebuy = 13.5,
        int meals = 0) =>
        new(id, name, buys ?? [Day(1), Day(14), Day(28)], outages ?? [], quantity, spend, unpriced, rebuy, meals);

    private static BacklogReport Find(params BacklogInput[] inputs) => BacklogSignals.Find(inputs, Today);

    [Fact]
    public void Bought_repeatedly_never_reported_out_and_now_gone_quiet_is_worth_checking()
    {
        var report = Find(Item());

        var finding = Assert.Single(report.Findings);
        Assert.Equal("Black Beans", finding.ProductName);
        Assert.Equal(3, finding.Trips);
        Assert.Equal(Day(1), finding.FirstBought);
        Assert.Equal(Day(28), finding.LastBought);
        Assert.Equal(1, report.Considered);
    }

    [Fact]
    public void Two_buys_are_one_rebuy_not_a_pattern()
    {
        // MinPurchases is 3: at two, "never ran out" reads the same whether you're stockpiling or
        // simply bought it twice. Such a product isn't judged at all, so it isn't in Considered either.
        var report = Find(Item(buys: [Day(1), Day(14)]));

        Assert.Empty(report.Findings);
        Assert.Equal(0, report.Considered);
    }

    [Fact]
    public void One_completed_cycle_disqualifies_even_though_the_burn_rate_is_still_null()
    {
        // THE reason BurnCycles is exposed rather than reading PredictionResult.BurnRateDays: the burn
        // rate needs TWO cycles, so it's null here — but this item demonstrably ran out once, which is
        // exactly the evidence the check is looking for the absence of.
        var report = Find(Item(outages: [Day(5)]));

        Assert.Empty(report.Findings);
        Assert.Equal(1, report.Considered); // it had the history to be judged; it just passed
        Assert.Equal(1, report.EverRanOut);
    }

    [Fact]
    public void An_outage_that_closes_no_cycle_does_not_disqualify()
    {
        // Before the first purchase there is no cycle for it to close — it says nothing about the
        // stock this item has accumulated since.
        var report = Find(Item(outages: [Day(1).AddDays(-3)]));

        Assert.Single(report.Findings);
        Assert.Equal(0, report.EverRanOut);
    }

    [Fact]
    public void An_outage_after_the_last_buy_does_disqualify()
    {
        // The final purchase's cycle has no next purchase to bound it, so a later outage closes it:
        // the household did report running out of this.
        Assert.Empty(Find(Item(outages: [Day(30)])).Findings);
    }

    [Fact]
    public void An_item_still_inside_its_rhythm_is_not_a_finding()
    {
        // The condition that makes this report mean anything. Against real data "never ran out" alone
        // flagged 26 of 27 regularly-bought products, because a household that rarely taps Out leaves
        // everything silent. Buying on schedule is not a backlog — going quiet is.
        Assert.Empty(Find(Item(rebuy: 45)).Findings);
        Assert.Single(Find(Item(rebuy: 29)).Findings); // last bought 30 days ago: one day past due
    }

    [Fact]
    public void Without_a_learned_rhythm_there_is_nothing_to_have_gone_quiet_against()
    {
        Assert.Empty(Find(Item(rebuy: null)).Findings);
    }

    [Fact]
    public void Overdue_days_measure_the_silence_past_the_rhythm()
    {
        // Last bought 30 days ago on a 13.5-day rhythm: 30 − 13 = 17 days of silence.
        var finding = Assert.Single(Find(Item()).Findings);

        Assert.Equal(30, finding.DaysSinceLastBought);
        Assert.Equal(17, finding.OverdueDays);
    }

    [Fact]
    public void Same_day_buys_collapse_into_one_trip()
    {
        // Occasions, not line items — the same collapse the engine does, because that's what the cycle
        // pairing sees. Four events on three days is three trips, and two days is below the minimum.
        var threeDays = Find(Item(buys: [Day(1), Day(1), Day(14), Day(28)]));
        Assert.Equal(3, Assert.Single(threeDays.Findings).Trips);

        var twoDays = Find(Item(buys: [Day(1), Day(1), Day(14), Day(14)]));
        Assert.Empty(twoDays.Findings);
    }

    [Fact]
    public void Unsorted_and_duplicated_dates_are_normalized_by_the_analysis()
    {
        // Callers may hand over raw event dates in whatever order EF returned them.
        var finding = Assert.Single(Find(Item(buys: [Day(28), Day(1), Day(14), Day(1)])).Findings);

        Assert.Equal(Day(1), finding.FirstBought);
        Assert.Equal(Day(28), finding.LastBought);
        Assert.Equal(3, finding.Trips);
    }

    [Fact]
    public void Spans_are_measured_to_today_not_to_the_last_buy()
    {
        var finding = Assert.Single(Find(Item()).Findings);

        Assert.Equal(Today.DayNumber - Day(1).DayNumber, finding.SpanDays);
        Assert.Equal(Today.DayNumber - Day(28).DayNumber, finding.DaysSinceLastBought);
    }

    [Fact]
    public void Findings_rank_by_money_committed_then_by_the_number_of_buys()
    {
        var report = Find(
            Item(id: 1, name: "Coffee", spend: 60m),
            Item(id: 2, name: "Beef", spend: 120m),
            Item(id: 3, name: "Rice", spend: 60m, buys: [Day(1), Day(7), Day(14), Day(21), Day(28)], rebuy: 7));

        // Beef leads on dollars; Rice beats Coffee on the same money with more buys behind it.
        Assert.Equal(
            new[] { "Beef", "Rice", "Coffee" },
            report.Findings.Select(f => f.ProductName).ToArray());
    }

    [Fact]
    public void An_unpriced_purchase_makes_the_spend_a_floor_and_says_so()
    {
        Assert.False(Assert.Single(Find(Item()).Findings).SpendIncomplete);
        Assert.True(Assert.Single(Find(Item(unpriced: 1)).Findings).SpendIncomplete);
    }

    [Fact]
    public void Outage_coverage_reports_how_much_evidence_the_silence_half_has()
    {
        // Disclosed, never a gate: at low coverage a missing OutNow means less, but the overdue half
        // stands on buying behaviour alone, so the finding is still shown — with the caveat.
        var report = Find(
            Item(id: 1),
            Item(id: 2, name: "Rice", outages: [Day(5)]),
            Item(id: 3, name: "Oats", outages: [Day(5)]),
            Item(id: 4, name: "Flour", outages: [Day(5)]));

        Assert.Equal(4, report.Considered);
        Assert.Equal(3, report.EverRanOut);
        Assert.Equal(0.75, report.OutageCoverage);
        Assert.Single(report.Findings);
    }

    [Fact]
    public void Recent_meal_uses_are_reported_and_never_scored()
    {
        // Evidence the item is moving, surfaced for the human — but the meal log only sees cooking that
        // went through a saved recipe, so its presence must not suppress the finding.
        Assert.Equal(4, Assert.Single(Find(Item(meals: 4)).Findings).RecentMealUses);
    }

    [Fact]
    public void Nothing_to_judge_is_an_empty_report_not_a_crash()
    {
        var report = BacklogSignals.Find([], Today);

        Assert.Empty(report.Findings);
        Assert.Equal(0, report.Considered);
        Assert.Equal(0, report.OutageCoverage);
    }
}
