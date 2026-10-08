namespace ShelfAware.Core.MealPlanning;

/// <summary>
/// Lays a plan's days out as calendar weeks for a month-grid view: full Sunday–Saturday rows, with padding
/// cells (null dates) before the first day and after the last so every week has seven columns. Pure date
/// math — the off-by-one-prone part of a calendar — kept here and unit-tested; the page maps meals onto the
/// dated cells.
/// </summary>
public static class MealCalendar
{
    /// <summary>The Sunday that starts <paramref name="day"/>'s calendar week. THE week boundary for every
    /// calendar in the app — the plan grid pads to it and the meal journal totals by it — so "this week"
    /// cannot mean Sunday on one screen and Monday on the next.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(int)day.DayOfWeek);

    /// <summary>The plan's days as weeks of seven cells (Sunday first). A cell is a <see cref="DateOnly"/> for
    /// a real day, or null for the leading/trailing padding that squares off the first and last weeks. Empty
    /// when <paramref name="days"/> &lt; 1.</summary>
    public static IReadOnlyList<IReadOnlyList<DateOnly?>> Weeks(DateOnly start, int days)
    {
        if (days < 1) return [];

        var cells = new List<DateOnly?>();
        for (var d = WeekStart(start); d < start; d = d.AddDays(1)) cells.Add(null); // lead-in to Sunday
        for (var d = 0; d < days; d++) cells.Add(start.AddDays(d));
        while (cells.Count % 7 != 0) cells.Add(null);                          // square off the last week

        return [.. cells.Chunk(7).Select(week => (IReadOnlyList<DateOnly?>)[.. week])];
    }
}
