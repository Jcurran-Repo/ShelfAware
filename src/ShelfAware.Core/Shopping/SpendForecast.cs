using ShelfAware.Core.Prediction;

namespace ShelfAware.Core.Shopping;

/// <summary>
/// How much one item is expected to cost inside a future window — the arithmetic behind Trends'
/// "next month" figure.
/// <para>It lives in Core rather than in <c>SpendInsight.razor</c> for the same reason
/// <c>ReportDataService</c>'s preset loads and <c>MealStock</c>'s decrement do (DESIGN.md §13.7): logic
/// private to a page is logic no test can reach, and this is the only place a COUNT changes a number
/// denominated in money. A silent regression here reads as the household's own spending changing.</para>
/// </summary>
public static class SpendForecast
{
    /// <summary>The first day this item is expected to need buying.
    /// <para>For a counted item that is when the shelf actually empties, NOT
    /// <see cref="PredictionResult.DueDate"/>: suppression deliberately leaves the due date alone so
    /// surfaces can explain themselves, which means a forecast stepping from it would bill the household
    /// for a purchase the app is at that moment telling them not to make. Falls back to the due date
    /// whenever there is no count in play — including when the caller didn't pass <c>honorQuantity</c>,
    /// since <see cref="PredictionResult.CountRunsOutOn"/> is null then too.</para></summary>
    public static DateOnly? FirstBuy(PredictionResult prediction) =>
        prediction.SuppressedByCount && prediction.CountRunsOutOn is { } exhausted
            ? exhausted
            : prediction.DueDate;

    /// <summary>Total expected cost for this item between <paramref name="windowStart"/> and
    /// <paramref name="windowEnd"/> inclusive: step from <paramref name="firstBuy"/> by
    /// <paramref name="intervalDays"/> and charge <paramref name="costPerBuy"/> for every landing inside
    /// the window that is also still in the future.
    /// <para>Days already past are skipped rather than back-charged — a due date the household has
    /// already sailed past is a reminder, not a second purchase. The step is clamped to at least one day
    /// so a degenerate interval can't spin, and the walk is bounded so a tiny interval against a wide
    /// window terminates.</para></summary>
    public static decimal InWindow(
        DateOnly firstBuy, double intervalDays, DateOnly today,
        DateOnly windowStart, DateOnly windowEnd, decimal costPerBuy)
    {
        var interval = Math.Max(1, (int)Math.Round(intervalDays));
        var total = 0m;
        var day = firstBuy;
        // The window is finite and the step is >= 1 day, so this is bounded by the window's own length;
        // the guard is a backstop against a caller passing an absurd window, not against the loop.
        for (var steps = 0; day <= windowEnd && steps < 4000; steps++)
        {
            if (day >= windowStart && day > today) total += costPerBuy;
            day = day.AddDays(interval);
        }
        return total;
    }
}
