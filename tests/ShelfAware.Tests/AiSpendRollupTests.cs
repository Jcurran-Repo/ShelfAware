using ShelfAware.Core.Billing;
using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class AiSpendRollupTests
{
    // A fixed "today" mid-month, so month/today boundaries are deterministic regardless of the run date.
    private static readonly DateOnly Today = new(2026, 3, 15);

    private static AiUsage Row(string household, DateOnly day, int calls = 0, long input = 0, long output = 0, long cost = 0) =>
        new() { HouseholdId = household, Day = day, Calls = calls, InputTokens = input, OutputTokens = output, CostMicros = cost };

    [Fact]
    public void Month_totals_sum_every_row_from_the_first_of_the_month()
    {
        // A row ON the 1st must count toward the month (the boundary is inclusive).
        var report = AiSpendRollup.Summarize(
        [
            Row("hh-a", Today, calls: 3, input: 100, output: 40, cost: 1_500_000),
            Row("hh-b", new DateOnly(2026, 3, 1), calls: 2, input: 10, output: 5, cost: 200_000),
        ], Today);

        Assert.Equal(5, report.Month.Calls);
        Assert.Equal(155, report.Month.Tokens);           // (100+40) + (10+5)
        Assert.Equal(1_700_000, report.Month.CostMicros);
        Assert.Equal(2, report.ActiveHouseholdsThisMonth);
    }

    [Fact]
    public void Today_is_only_todays_rows_within_the_month()
    {
        var report = AiSpendRollup.Summarize(
        [
            Row("hh-a", Today, calls: 3),
            Row("hh-a", new DateOnly(2026, 3, 14), calls: 7), // this month, not today
        ], Today);

        Assert.Equal(3, report.Today.Calls);  // yesterday's 7 is NOT in today
        Assert.Equal(10, report.Month.Calls); // but both are in the month
    }

    [Fact]
    public void Earlier_months_are_excluded_from_every_figure()
    {
        var report = AiSpendRollup.Summarize(
        [
            Row("hh-a", Today, cost: 1_000_000),
            Row("hh-c", new DateOnly(2026, 2, 28), cost: 9_000_000), // last month
        ], Today);

        Assert.Equal(1_000_000, report.Month.CostMicros);     // February's row is not in March
        Assert.Equal(1_000_000, report.Today.CostMicros);
        Assert.Equal(1, report.ActiveHouseholdsThisMonth);    // hh-c's only row is out of the month
    }

    [Fact]
    public void A_future_dated_row_is_not_counted_in_the_month()
    {
        // Usage rows are never future-dated in practice (the meter stamps today), but the rollup is the
        // window authority, so a stray future row must not inflate the month figure.
        var report = AiSpendRollup.Summarize(
        [
            Row("hh-a", Today, calls: 3, cost: 1_000_000),
            Row("hh-b", new DateOnly(2026, 3, 20), calls: 9, cost: 9_000_000), // this month, but AFTER today (the 15th)
        ], Today);

        Assert.Equal(3, report.Month.Calls);                  // the future row is excluded
        Assert.Equal(1_000_000, report.Month.CostMicros);
        Assert.Equal(1, report.ActiveHouseholdsThisMonth);    // hh-b's only row is future → not active
    }

    [Fact]
    public void Tokens_are_input_plus_output()
    {
        // Distinct input/output so a dropped operand (100 or 40) or a swap-to-minus (60) is caught.
        var report = AiSpendRollup.Summarize([Row("hh-a", Today, input: 100, output: 40)], Today);

        Assert.Equal(140, report.Today.Tokens);
        Assert.Equal(140, report.Month.Tokens);
    }

    [Fact]
    public void Active_households_counts_distinct_households_not_rows()
    {
        var report = AiSpendRollup.Summarize(
        [
            Row("hh-a", Today),
            Row("hh-a", new DateOnly(2026, 3, 10)), // same household, second day
            Row("hh-b", new DateOnly(2026, 3, 5)),
        ], Today);

        Assert.Equal(2, report.ActiveHouseholdsThisMonth); // hh-a and hh-b, not 3 rows
    }

    [Fact]
    public void No_rows_is_all_zeros()
    {
        var report = AiSpendRollup.Summarize([], Today);

        Assert.Equal(new AiSpendSummary(0, 0, 0), report.Today);
        Assert.Equal(new AiSpendSummary(0, 0, 0), report.Month);
        Assert.Equal(0, report.ActiveHouseholdsThisMonth);
    }
}
