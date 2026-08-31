using ShelfAware.Core.MealPlanning;

namespace ShelfAware.Tests;

/// <summary>The month-grid layout: full Sunday–Saturday weeks with null padding around the plan's days.</summary>
public class MealCalendarTests
{
    [Fact]
    public void Every_week_has_seven_cells_and_the_dated_ones_are_the_plan_days_in_order()
    {
        var start = new DateOnly(2026, 3, 4); // a Wednesday
        var weeks = MealCalendar.Weeks(start, 10);

        Assert.All(weeks, w => Assert.Equal(7, w.Count));
        var dated = weeks.SelectMany(w => w).Where(c => c is not null).Select(c => c!.Value).ToList();
        Assert.Equal(Enumerable.Range(0, 10).Select(start.AddDays), dated);
    }

    [Fact]
    public void The_first_day_sits_in_its_weekday_column_with_leading_padding()
    {
        var start = new DateOnly(2026, 3, 4); // Wednesday → 3 leading pad cells (Sun, Mon, Tue)
        var firstWeek = MealCalendar.Weeks(start, 7)[0];

        Assert.Null(firstWeek[0]); // Sun
        Assert.Null(firstWeek[1]); // Mon
        Assert.Null(firstWeek[2]); // Tue
        Assert.Equal(start, firstWeek[3]); // Wed
    }

    [Fact]
    public void A_sunday_start_has_no_leading_padding()
    {
        var sunday = new DateOnly(2026, 3, 1); // a Sunday
        Assert.Equal(sunday, MealCalendar.Weeks(sunday, 7)[0][0]);
    }

    [Fact]
    public void The_last_week_is_padded_out_to_seven()
    {
        var weeks = MealCalendar.Weeks(new DateOnly(2026, 3, 1), 30); // Sun start, 30 days
        var last = weeks[^1];
        Assert.Equal(7, last.Count);
        Assert.Null(last[^1]); // trailing padding exists (30 isn't a multiple of 7)
    }

    [Fact]
    public void A_non_positive_day_count_yields_no_weeks()
    {
        Assert.Empty(MealCalendar.Weeks(new DateOnly(2026, 3, 4), 0));
    }
}
