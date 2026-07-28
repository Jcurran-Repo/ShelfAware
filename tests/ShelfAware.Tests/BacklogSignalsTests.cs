using ShelfAware.Core.Reporting;

namespace ShelfAware.Tests;

public class BacklogSignalsTests
{
    private static readonly DateOnly Today = new(2026, 7, 28);

    /// <summary>Day N of the history, counting from 1 June — so the whole story sits before "today"
    /// and the numbers in the tests read as a calendar rather than as arithmetic.</summary>
    private static DateOnly Day(int day) => new DateOnly(2026, 6, 1).AddDays(day - 1);

    /// <summary>The default item: bought on days 1, 14 and 28 with a ~13-day rhythm, and a due date the
    /// engine put on day 41 — a fortnight before "today", so it is well past due. The shape every
    /// condition is varied against.</summary>
    private static BacklogInput Item(
        int id = 1,
        string name = "Black Beans",
        DateOnly[]? buys = null,
        DateOnly[]? outages = null,
        decimal quantity = 3,
        decimal spend = 30m,
        int unpriced = 0,
        double? rebuy = 13.5,
        DateOnly? due = null,
        string? unit = null,
        int meals = 0) =>
        new(id, name, unit, buys ?? [Day(1), Day(14), Day(28)], outages ?? [], quantity, spend, unpriced,
            rebuy, due ?? Day(41), meals);

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
    public void An_item_the_engine_does_not_call_due_is_not_a_finding()
    {
        // The condition that makes this report mean anything. Against real data "never ran out" alone
        // flagged 26 of 27 regularly-bought products, because a household that rarely taps Out leaves
        // everything silent. Being due is the half that needs no button.
        Assert.Empty(Find(Item(due: Today.AddDays(5))).Findings);
        Assert.Empty(Find(Item(due: Today)).Findings);           // due TODAY is not yet overdue
        Assert.Single(Find(Item(due: Today.AddDays(-1))).Findings);
    }

    [Fact]
    public void The_canonical_hoard_three_months_of_beef_is_found()
    {
        // The case the whole report exists for, written out so the intent can't drift: bought one at a
        // time, then a freezer-filling trip, then months of silence with nothing ever marked out. The
        // engine stretches the due date for a stock-up but caps at 3×, so a 6× buy still goes overdue —
        // the grocery list starts asking for a roast the freezer is still full of, and this is what
        // argues back.
        var report = Find(Item(
            name: "Beef Chuck Roast",
            buys: [Day(1), Day(15), Day(29), Day(43), Day(71)],
            outages: [],
            quantity: 10,
            spend: 189.40m,
            rebuy: 14,
            due: Today.AddDays(-46)));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(5, finding.Trips);
        Assert.Equal(46, finding.OverdueDays);
        Assert.Equal(189.40m, finding.Spend);
        Assert.Equal(0, report.EverRanOut);
    }

    [Fact]
    public void A_stock_up_that_pushed_the_due_date_out_is_respected()
    {
        // The regression. A hand-rolled "days since last buy > rebuy median" called an item overdue
        // while the product page called it Stocked for five more days, because the engine's StockUpFactor
        // had stretched its due date for a 1.5× buy — the app's own "you bought extra, it lasts longer"
        // logic, which is exactly what a backlog report must not override. Ask the engine; never
        // re-derive. Here: last bought 30 days ago on a ~13-day rhythm, but the engine says due in 5.
        Assert.Empty(Find(Item(rebuy: 13.5, due: Today.AddDays(5))).Findings);
    }

    [Fact]
    public void Without_a_due_date_the_engine_is_still_learning_and_there_is_nothing_to_say()
    {
        // `with` rather than the helper's optional parameter: `due ?? Day(41)` can't tell "not specified"
        // from "explicitly none", and this test is about the second one.
        Assert.Empty(Find(Item() with { DueDate = null }).Findings);
    }

    [Fact]
    public void Overdue_days_come_from_the_engines_due_date()
    {
        // Due on day 41 (11 July), today is 28 July: 17 days overdue — the same number the dashboard
        // and product page show. Days since the last buy is a separate, larger number.
        var finding = Assert.Single(Find(Item()).Findings);

        Assert.Equal(17, finding.OverdueDays);
        Assert.Equal(30, finding.DaysSinceLastBought);
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
