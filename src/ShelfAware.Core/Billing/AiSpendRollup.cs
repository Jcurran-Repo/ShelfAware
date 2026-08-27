using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Billing;

/// <summary>Rolled-up AI consumption — calls, tokens (input + output), and cost in micros — over some
/// set of daily <see cref="AiUsage"/> rows. The unit the admin dashboard shows for "today" and "this
/// month".</summary>
public sealed record AiSpendSummary(int Calls, long Tokens, long CostMicros);

/// <summary>The operator's cross-household AI picture: today, this calendar month, and how many
/// households were actually active this month (used the AI, not merely registered).</summary>
public sealed record AiSpendReport(AiSpendSummary Today, AiSpendSummary Month, int ActiveHouseholdsThisMonth);

/// <summary>
/// Splits a set of daily <see cref="AiUsage"/> rows into today's and this month's totals — the pure
/// aggregation behind the admin dashboard's "at a glance" AI figures. It lives in Core, and takes
/// <c>today</c> as a parameter rather than reading the clock, precisely so the windowing can be pinned
/// deterministically: the rows it sums come from the app's cross-household read (<c>AdminAiSpendReader</c>),
/// so the split has to be exhaustively tested, not eyeballed.
/// </summary>
public static class AiSpendRollup
{
    /// <summary>Window <paramref name="rows"/> against <paramref name="today"/>: "this month" is the 1st
    /// of today's month through today (month-to-date), "today" is exactly that day. Both ends are the
    /// authority here — a caller that passes a wider set (earlier months, or a stray future-dated row) still
    /// gets a correct month figure — so the reader's own WHERE is only a load bound. "Active this month" is
    /// the count of distinct households with any row in the month.</summary>
    public static AiSpendReport Summarize(IEnumerable<AiUsage> rows, DateOnly today)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthRows = rows.Where(r => r.Day >= monthStart && r.Day <= today).ToList();
        var todayRows = monthRows.Where(r => r.Day == today).ToList();
        var activeHouseholds = monthRows.Select(r => r.HouseholdId).Distinct().Count();
        return new AiSpendReport(Sum(todayRows), Sum(monthRows), activeHouseholds);
    }

    private static AiSpendSummary Sum(IReadOnlyCollection<AiUsage> rows) => new(
        rows.Sum(r => r.Calls),
        rows.Sum(r => r.InputTokens + r.OutputTokens),
        rows.Sum(r => r.CostMicros));
}
