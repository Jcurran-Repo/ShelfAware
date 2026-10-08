using System.Globalization;
using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;

namespace ShelfAware.Core.Journal;

/// <summary>The three spans the journal totals over.</summary>
public enum JournalPeriod { Day, Week, Month }

/// <summary>An inclusive run of days.</summary>
public readonly record struct DateSpan(DateOnly From, DateOnly To)
{
    public bool Contains(DateOnly day) => day >= From && day <= To;
}

/// <summary>Calories over some set of journal entries, with the two caveats every total has to carry: how
/// many items had no calorie count (left out, never counted as zero), and whether any counted number was
/// an estimate rather than something the person stated.</summary>
/// <param name="Calories">The sum over the entries that HAVE a calorie count.</param>
/// <param name="Counted">How many entries went into <paramref name="Calories"/>.</param>
/// <param name="Uncounted">How many entries had no calorie count and are not in the sum.</param>
/// <param name="Estimated">True when at least one counted entry was an estimate.</param>
public sealed record CalorieTotal(int Calories, int Counted, int Uncounted, bool Estimated)
{
    public static readonly CalorieTotal None = new(0, 0, 0, false);

    /// <summary>Every entry the total looked at, counted or not.</summary>
    public int Items => Counted + Uncounted;

    /// <summary>"~1,850 kcal" when any part is an estimate, "1,850 kcal" when every part was stated, and null
    /// when nothing was counted — a blank, not a "0 kcal" that would read as a day of fasting.</summary>
    public string? Kcal => Counted == 0
        ? null
        : $"{(Estimated ? "~" : "")}{MealJournal.Number(Calories)} kcal";

    /// <summary><see cref="Kcal"/>, or "no calorie count" where a phrase has to say something — one food's
    /// line, or Reginald's answer.</summary>
    public string KcalOrUncounted => Kcal ?? "no calorie count";

    /// <summary>"1 item" / "3 items" — every entry looked at.</summary>
    public string ItemsText => Items == 1 ? "1 item" : $"{Items} items";

    /// <summary>"2 items have no calorie count", or null when every item had one.</summary>
    public string? UncountedNote => Uncounted == 0
        ? null
        : $"{Uncounted} {(Uncounted == 1 ? "item has" : "items have")} no calorie count";
}

/// <summary>One meal on one day: its slot, the foods in it in the order they were logged, and their total.</summary>
public sealed record JournalMeal(MealSlot Slot, IReadOnlyList<JournalEntry> Items, CalorieTotal Total);

/// <summary>
/// The meal journal's arithmetic and its rules, in one place. Every surface that shows or speaks a journal
/// number — the calendar's day cells, its week column, the month header, the day's per-meal list, and
/// Reginald's answer to "how many calories this week?" — asks this class, so the page and the voice cannot
/// total the same week two ways. Pure: no clock, no I/O; "today" is passed in.
/// </summary>
public static class MealJournal
{
    /// <summary>The most calories one entry may claim. Generous — a whole large pizza is ~2,500 — so it
    /// only stops a slip ("15000" for "150"), never a real meal.</summary>
    public const int MaxCalories = 10_000;

    /// <summary>The longest food description an entry keeps.</summary>
    public const int MaxFoodLength = 200;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The day, the calendar week (Sunday–Saturday, <see cref="MealCalendar.WeekStart"/>), or the
    /// calendar month that <paramref name="day"/> falls in.</summary>
    public static DateSpan SpanOf(JournalPeriod period, DateOnly day)
    {
        switch (period)
        {
            case JournalPeriod.Day:
                return new DateSpan(day, day);
            case JournalPeriod.Week:
                var sunday = MealCalendar.WeekStart(day);
                return new DateSpan(sunday, sunday.AddDays(6));
            case JournalPeriod.Month:
                var first = new DateOnly(day.Year, day.Month, 1);
                return new DateSpan(first, first.AddMonths(1).AddDays(-1));
            default:
                throw new ArgumentOutOfRangeException(nameof(period), period, "Not a journal period.");
        }
    }

    /// <summary>The total over every entry given.</summary>
    public static CalorieTotal Total(IEnumerable<JournalEntry> entries)
    {
        int calories = 0, counted = 0, uncounted = 0;
        var estimated = false;
        foreach (var e in entries)
        {
            if (e.Calories is { } c)
            {
                calories += c;
                counted++;
                estimated |= e.CaloriesEstimated;
            }
            else
            {
                uncounted++;
            }
        }
        return new CalorieTotal(calories, counted, uncounted, estimated);
    }

    /// <summary>The entries eaten inside <paramref name="span"/> — the one reading of "which meals were on
    /// that day", for the totals and the page's day list alike.</summary>
    public static IEnumerable<JournalEntry> In(IEnumerable<JournalEntry> entries, DateSpan span) =>
        entries.Where(e => span.Contains(e.EatenOn));

    /// <summary>The total over the entries eaten inside <paramref name="span"/>.</summary>
    public static CalorieTotal Total(IEnumerable<JournalEntry> entries, DateSpan span) => Total(In(entries, span));

    /// <summary>A meal named inside a sentence: "lunch".</summary>
    public static string SlotName(MealSlot slot) => slot.ToString().ToLowerInvariant();

    /// <summary>One day's entries as meals: only the slots that have something in them, in the order the
    /// day is eaten (breakfast, lunch, dinner, snack), each meal's foods in the order they were logged.</summary>
    public static IReadOnlyList<JournalMeal> Meals(IEnumerable<JournalEntry> dayEntries) =>
    [
        .. dayEntries
            .GroupBy(e => e.Slot)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                IReadOnlyList<JournalEntry> items = [.. g.OrderBy(e => e.LoggedAt).ThenBy(e => e.Id)];
                return new JournalMeal(g.Key, items, Total(items));
            }),
    ];

    /// <summary>The month as whole calendar weeks, Sunday first — every cell a real date, including the
    /// days of the neighbouring months that square off the first and last rows. Real dates rather than
    /// padding because the journal's week column totals the WHOLE week: a row's total that silently left
    /// out its Sunday because Sunday was in last month would be a week total that isn't one.</summary>
    public static IReadOnlyList<IReadOnlyList<DateOnly>> MonthGrid(int year, int month)
    {
        var month1 = SpanOf(JournalPeriod.Month, new DateOnly(year, month, 1));
        var weeks = new List<IReadOnlyList<DateOnly>>();
        for (var sunday = MealCalendar.WeekStart(month1.From); sunday <= month1.To; sunday = sunday.AddDays(7))
            weeks.Add([.. Enumerable.Range(0, 7).Select(sunday.AddDays)]);
        return weeks;
    }

    /// <summary>Why an entry can't be written as given, or null when it can. THE rule for every way in —
    /// Reginald's tool, the page's add form and its edit — because the write path asks it, not the callers.</summary>
    public static string? Problem(string? food, int? calories, DateOnly eatenOn, DateOnly today)
    {
        var trimmed = food?.Trim() ?? "";
        if (trimmed.Length == 0) return "Say what was eaten.";
        if (trimmed.Length > MaxFoodLength) return $"Keep the food to {MaxFoodLength} characters or fewer.";
        if (calories < 0) return "Calories can't be negative.";
        if (calories > MaxCalories) return $"{Number(calories.Value)} calories is more than one entry can hold (the most is {Number(MaxCalories)}) — split it into the foods it was.";
        if (eatenOn > today) return "That day hasn't happened yet — the journal only records what was already eaten.";
        return null;
    }

    /// <summary>How a span is named, on the page's headings and in Reginald's answers alike.</summary>
    public static string Label(JournalPeriod period, DateSpan span) => period switch
    {
        JournalPeriod.Day => span.From.ToString("dddd, MMM d", Invariant),
        JournalPeriod.Week => span.From.Month == span.To.Month
            ? $"the week of {span.From.ToString("MMM d", Invariant)}–{span.To.Day.ToString(Invariant)}"
            : $"the week of {span.From.ToString("MMM d", Invariant)} – {span.To.ToString("MMM d", Invariant)}",
        JournalPeriod.Month => span.From.ToString("MMMM yyyy", Invariant),
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Not a journal period."),
    };

    /// <summary>A total stated as a fact for Reginald to relay — every caveat included, so the spoken answer
    /// cannot drop the estimate or the uncounted items the page shows beside the same number.</summary>
    public static string Describe(JournalPeriod period, DateSpan span, CalorieTotal total)
    {
        var label = Label(period, span);
        if (total.Items == 0) return $"Nothing is logged for {label}.";
        var items = total.ItemsText;
        var sentence = total.Counted == 0
            ? $"For {label}: {items} logged, none with a calorie count."
            : $"For {label}: {(total.Estimated ? "about " : "")}{Number(total.Calories)} calories across {items}"
              + (total.Estimated ? " (some are estimates)." : ".");
        if (total.Counted == 0 || total.Uncounted == 0) return sentence;
        return total.Uncounted == 1
            ? $"{sentence} 1 item has no calorie count and isn't included."
            : $"{sentence} {total.Uncounted} items have no calorie count and aren't included.";
    }

    /// <summary>Thousands-separated, culture-invariant — the one way a journal number is written.</summary>
    public static string Number(int value) => value.ToString("#,0", Invariant);
}
