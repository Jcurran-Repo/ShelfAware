using ShelfAware.Core.Domain;
using ShelfAware.Core.Journal;
using ShelfAware.Core.MealPlanning;

namespace ShelfAware.Tests;

/// <summary>The meal journal's arithmetic and rules — the one place every journal number on screen and in
/// Reginald's answers comes from.</summary>
public class MealJournalTests
{
    private static readonly DateOnly Thursday = new(2026, 10, 8);

    private static int _nextId;

    private static JournalEntry E(
        DateOnly day, int? calories, bool estimated = false, MealSlot slot = MealSlot.Lunch,
        string food = "Sandwich", int minute = 0) =>
        new()
        {
            Id = ++_nextId, EatenOn = day, Calories = calories, CaloriesEstimated = estimated, Slot = slot,
            Food = food, LoggedAt = new DateTimeOffset(2026, 10, 8, 12, minute, 0, TimeSpan.Zero),
        };

    // --- spans ----------------------------------------------------------------------------------

    [Fact]
    public void A_day_span_is_just_that_day()
    {
        Assert.Equal(new DateSpan(Thursday, Thursday), MealJournal.SpanOf(JournalPeriod.Day, Thursday));
    }

    [Theory]
    [InlineData(2026, 10, 4)]  // the Sunday itself
    [InlineData(2026, 10, 8)]  // midweek
    [InlineData(2026, 10, 10)] // the Saturday
    public void A_week_runs_sunday_to_saturday_around_any_of_its_days(int y, int m, int d)
    {
        Assert.Equal(new DateSpan(new(2026, 10, 4), new(2026, 10, 10)),
            MealJournal.SpanOf(JournalPeriod.Week, new DateOnly(y, m, d)));
    }

    [Fact]
    public void The_week_boundary_is_the_meal_plans_week_boundary()
    {
        // One definition of "this week" across both calendars.
        Assert.Equal(MealCalendar.WeekStart(Thursday), MealJournal.SpanOf(JournalPeriod.Week, Thursday).From);
    }

    [Theory]
    [InlineData(2026, 10, 8, 31)]
    [InlineData(2028, 2, 14, 29)] // leap February
    [InlineData(2026, 2, 1, 28)]
    [InlineData(2026, 12, 31, 31)] // December's end must not roll into January
    public void A_month_runs_from_the_first_to_its_last_day(int y, int m, int d, int lastDay)
    {
        Assert.Equal(new DateSpan(new(y, m, 1), new(y, m, lastDay)),
            MealJournal.SpanOf(JournalPeriod.Month, new DateOnly(y, m, d)));
    }

    [Fact]
    public void An_unknown_period_is_refused_rather_than_guessed()
    {
        var span = Assert.Throws<ArgumentOutOfRangeException>(() => MealJournal.SpanOf((JournalPeriod)9, Thursday));
        var label = Assert.Throws<ArgumentOutOfRangeException>(() => MealJournal.Label((JournalPeriod)9, new(Thursday, Thursday)));
        Assert.StartsWith("Not a journal period.", span.Message);
        Assert.StartsWith("Not a journal period.", label.Message);
    }

    [Fact]
    public void A_span_contains_both_of_its_ends_and_nothing_outside_them()
    {
        var span = new DateSpan(new(2026, 10, 4), new(2026, 10, 10));
        Assert.True(span.Contains(new(2026, 10, 4)));
        Assert.True(span.Contains(new(2026, 10, 10)));
        Assert.False(span.Contains(new(2026, 10, 3)));
        Assert.False(span.Contains(new(2026, 10, 11)));
    }

    [Fact]
    public void Week_start_is_the_sunday_on_or_before_the_day()
    {
        Assert.Equal(new DateOnly(2026, 10, 4), MealCalendar.WeekStart(new(2026, 10, 4)));  // a Sunday
        Assert.Equal(new DateOnly(2026, 10, 4), MealCalendar.WeekStart(new(2026, 10, 10))); // a Saturday
        Assert.Equal(new DateOnly(2026, 9, 27), MealCalendar.WeekStart(new(2026, 10, 1)));  // across a month
    }

    // --- totals ---------------------------------------------------------------------------------

    [Fact]
    public void A_total_sums_the_counted_entries_and_counts_the_rest_separately()
    {
        var total = MealJournal.Total([E(Thursday, 400), E(Thursday, 250), E(Thursday, null)]);

        Assert.Equal(new CalorieTotal(650, 2, 1, false), total);
        Assert.Equal(3, total.Items);
    }

    [Fact]
    public void One_estimated_entry_marks_the_whole_total_as_an_estimate()
    {
        Assert.True(MealJournal.Total([E(Thursday, 400), E(Thursday, 250, estimated: true)]).Estimated);
        Assert.True(MealJournal.Total([E(Thursday, 250, estimated: true), E(Thursday, 400)]).Estimated);
        // Two estimates are still an estimate — the flag accumulates, it doesn't toggle.
        Assert.True(MealJournal.Total([E(Thursday, 250, estimated: true), E(Thursday, 400, estimated: true)]).Estimated);
    }

    [Fact]
    public void An_uncounted_entry_cannot_make_a_total_an_estimate()
    {
        // Its flag says nothing about a number that isn't in the sum.
        Assert.False(MealJournal.Total([E(Thursday, 400), E(Thursday, null, estimated: true)]).Estimated);
    }

    [Fact]
    public void Nothing_totals_to_none()
    {
        Assert.Equal(CalorieTotal.None, MealJournal.Total([]));
        Assert.Equal(0, CalorieTotal.None.Items);
    }

    [Fact]
    public void A_span_total_counts_both_ends_and_nothing_outside()
    {
        var week = MealJournal.SpanOf(JournalPeriod.Week, Thursday);
        JournalEntry[] entries =
        [
            E(new(2026, 10, 3), 1000),  // Saturday before
            E(new(2026, 10, 4), 100),   // Sunday — in
            E(new(2026, 10, 10), 10),   // Saturday — in
            E(new(2026, 10, 11), 5000), // Sunday after
        ];

        Assert.Equal(110, MealJournal.Total(entries, week).Calories);
    }

    [Theory]
    [InlineData(1850, false, "1,850 kcal")]
    [InlineData(1850, true, "~1,850 kcal")]
    [InlineData(0, false, "0 kcal")] // a real zero (black coffee) is a count, not a blank
    public void Kcal_writes_the_number_and_marks_an_estimate(int calories, bool estimated, string expected)
    {
        Assert.Equal(expected, MealJournal.Total([E(Thursday, calories, estimated)]).Kcal);
    }

    [Fact]
    public void Kcal_is_blank_when_nothing_was_counted_rather_than_a_misleading_zero()
    {
        Assert.Null(MealJournal.Total([E(Thursday, null)]).Kcal);
        Assert.Null(CalorieTotal.None.Kcal);
    }

    [Fact]
    public void The_uncounted_note_says_how_many_and_agrees_in_number()
    {
        Assert.Null(MealJournal.Total([E(Thursday, 100)]).UncountedNote);
        Assert.Equal("1 item has no calorie count", MealJournal.Total([E(Thursday, null)]).UncountedNote);
        Assert.Equal("2 items have no calorie count", MealJournal.Total([E(Thursday, null), E(Thursday, null)]).UncountedNote);
    }

    // --- meals ----------------------------------------------------------------------------------

    [Fact]
    public void A_day_reads_as_its_meals_in_eating_order_with_empty_slots_left_out()
    {
        var meals = MealJournal.Meals(
        [
            E(Thursday, 300, slot: MealSlot.Dinner),
            E(Thursday, 150, slot: MealSlot.Snack),
            E(Thursday, 200, slot: MealSlot.Breakfast),
            E(Thursday, 100, slot: MealSlot.Breakfast),
        ]);

        Assert.Equal([MealSlot.Breakfast, MealSlot.Dinner, MealSlot.Snack], meals.Select(m => m.Slot));
        Assert.Equal(300, meals[0].Total.Calories);
        Assert.Equal(2, meals[0].Items.Count);
    }

    [Fact]
    public void A_meals_foods_are_in_the_order_they_were_logged()
    {
        var later = E(Thursday, 1, food: "Toast", minute: 30);
        var earlier = E(Thursday, 1, food: "Eggs", minute: 5);
        var sameMinuteFirst = E(Thursday, 1, food: "Coffee", minute: 30);
        var sameMinuteSecond = E(Thursday, 1, food: "Juice", minute: 30);

        var meal = Assert.Single(MealJournal.Meals([later, sameMinuteSecond, earlier, sameMinuteFirst]));

        // LoggedAt first; the id breaks a tie in the order the rows were written.
        Assert.Equal(["Eggs", "Toast", "Coffee", "Juice"], meal.Items.Select(i => i.Food));
    }

    // --- the month grid -------------------------------------------------------------------------

    [Fact]
    public void The_month_grid_is_whole_weeks_squared_off_with_real_neighbouring_days()
    {
        // October 2026 starts on a Thursday and ends on a Saturday.
        var weeks = MealJournal.MonthGrid(2026, 10);

        Assert.Equal(5, weeks.Count);
        Assert.Equal(new DateOnly(2026, 9, 27), weeks[0][0]);
        Assert.Equal(new DateOnly(2026, 10, 31), weeks[^1][^1]);
        var days = weeks.SelectMany(w => w).ToList();
        Assert.All(weeks, w => Assert.Equal(7, w.Count));
        Assert.Equal(Enumerable.Range(0, days.Count).Select(days[0].AddDays), days); // consecutive, no gaps
        Assert.All(weeks, w => Assert.Equal(DayOfWeek.Sunday, w[0].DayOfWeek));
    }

    [Fact]
    public void A_month_that_starts_on_sunday_and_ends_on_saturday_needs_no_neighbours()
    {
        var weeks = MealJournal.MonthGrid(2026, 2); // Feb 1 2026 is a Sunday; the 28th a Saturday

        Assert.Equal(4, weeks.Count);
        Assert.Equal(new DateOnly(2026, 2, 1), weeks[0][0]);
        Assert.Equal(new DateOnly(2026, 2, 28), weeks[^1][^1]);
    }

    [Fact]
    public void A_month_ending_on_sunday_gets_a_row_for_that_last_day()
    {
        var weeks = MealJournal.MonthGrid(2026, 5); // May 31 2026 is a Sunday

        Assert.Equal(new DateOnly(2026, 5, 31), weeks[^1][0]);
        Assert.Equal(new DateOnly(2026, 6, 6), weeks[^1][^1]);
    }

    // --- the write rule -------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_food_is_refused(string? food)
    {
        Assert.Equal("Say what was eaten.", MealJournal.Problem(food, 100, Thursday, Thursday));
    }

    [Fact]
    public void The_food_length_limit_is_inclusive_and_measured_after_trimming()
    {
        var atLimit = new string('a', MealJournal.MaxFoodLength);
        Assert.Null(MealJournal.Problem(atLimit, 1, Thursday, Thursday));
        Assert.Null(MealJournal.Problem($"  {atLimit}  ", 1, Thursday, Thursday));
        Assert.Equal("Keep the food to 200 characters or fewer.",
            MealJournal.Problem(atLimit + "a", 1, Thursday, Thursday));
    }

    [Fact]
    public void Calories_may_be_unknown_zero_or_up_to_the_limit()
    {
        Assert.Null(MealJournal.Problem("Toast", null, Thursday, Thursday));
        Assert.Null(MealJournal.Problem("Water", 0, Thursday, Thursday));
        Assert.Null(MealJournal.Problem("Feast", MealJournal.MaxCalories, Thursday, Thursday));
    }

    [Fact]
    public void Negative_calories_are_refused()
    {
        Assert.Equal("Calories can't be negative.", MealJournal.Problem("Toast", -1, Thursday, Thursday));
    }

    [Fact]
    public void Calories_past_the_limit_are_refused_with_a_way_forward()
    {
        Assert.Equal(
            "10,001 calories is more than one entry can hold (the most is 10,000) — split it into the foods it was.",
            MealJournal.Problem("Feast", MealJournal.MaxCalories + 1, Thursday, Thursday));
    }

    [Fact]
    public void A_day_that_has_not_happened_is_refused_and_today_is_not()
    {
        Assert.Null(MealJournal.Problem("Toast", 100, Thursday, today: Thursday));
        Assert.Equal("That day hasn't happened yet — the journal only records what was already eaten.",
            MealJournal.Problem("Toast", 100, Thursday.AddDays(1), today: Thursday));
    }

    // --- words ----------------------------------------------------------------------------------

    [Fact]
    public void Spans_are_named_the_same_way_everywhere()
    {
        Assert.Equal("Thursday, Oct 8", MealJournal.Label(JournalPeriod.Day, MealJournal.SpanOf(JournalPeriod.Day, Thursday)));
        Assert.Equal("the week of Oct 4–10", MealJournal.Label(JournalPeriod.Week, MealJournal.SpanOf(JournalPeriod.Week, Thursday)));
        Assert.Equal("the week of Sep 27 – Oct 3",
            MealJournal.Label(JournalPeriod.Week, MealJournal.SpanOf(JournalPeriod.Week, new(2026, 10, 1))));
        Assert.Equal("October 2026", MealJournal.Label(JournalPeriod.Month, MealJournal.SpanOf(JournalPeriod.Month, Thursday)));
    }

    private static string Describe(JournalPeriod period, params JournalEntry[] entries) =>
        MealJournal.Describe(period, MealJournal.SpanOf(period, Thursday), MealJournal.Total(entries));

    [Fact]
    public void Describing_an_empty_span_says_nothing_is_logged()
    {
        Assert.Equal("Nothing is logged for the week of Oct 4–10.", Describe(JournalPeriod.Week));
    }

    [Fact]
    public void Describing_a_stated_total_has_no_estimate_hedge()
    {
        Assert.Equal("For Thursday, Oct 8: 1,200 calories across 2 items.",
            Describe(JournalPeriod.Day, E(Thursday, 700), E(Thursday, 500)));
    }

    [Fact]
    public void Describing_an_estimated_total_keeps_the_hedge_the_page_shows_as_a_tilde()
    {
        Assert.Equal("For October 2026: about 1,200 calories across 2 items (some are estimates).",
            Describe(JournalPeriod.Month, E(Thursday, 700, estimated: true), E(Thursday, 500)));
    }

    [Fact]
    public void Describing_a_single_item_is_singular()
    {
        Assert.Equal("For Thursday, Oct 8: 700 calories across 1 item.", Describe(JournalPeriod.Day, E(Thursday, 700)));
    }

    [Fact]
    public void Describing_discloses_what_was_left_out_of_the_number()
    {
        Assert.Equal("For Thursday, Oct 8: 700 calories across 2 items. 1 item has no calorie count and isn't included.",
            Describe(JournalPeriod.Day, E(Thursday, 700), E(Thursday, null)));
        Assert.Equal("For Thursday, Oct 8: 700 calories across 3 items. 2 items have no calorie count and aren't included.",
            Describe(JournalPeriod.Day, E(Thursday, 700), E(Thursday, null), E(Thursday, null)));
    }

    [Fact]
    public void Describing_a_span_with_no_counted_items_never_claims_a_number()
    {
        Assert.Equal("For Thursday, Oct 8: 2 items logged, none with a calorie count.",
            Describe(JournalPeriod.Day, E(Thursday, null), E(Thursday, null)));
        Assert.Equal("For Thursday, Oct 8: 1 item logged, none with a calorie count.",
            Describe(JournalPeriod.Day, E(Thursday, null)));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1,000")]
    [InlineData(1234567, "1,234,567")]
    public void Numbers_are_thousands_separated_regardless_of_culture(int value, string expected)
    {
        Assert.Equal(expected, MealJournal.Number(value));
    }
}
