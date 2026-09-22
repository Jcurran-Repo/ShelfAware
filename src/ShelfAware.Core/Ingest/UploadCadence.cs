namespace ShelfAware.Core.Ingest;

/// <summary>
/// One household's own receipt-upload rhythm, and the single answer to "should we nudge them to upload
/// one?".
/// <para>The cadence is LEARNED, never a constant: a household that shops on Saturdays is quiet for six
/// days as a matter of routine, and one that shops on the way home most evenings is doing something
/// unusual by day three. A fixed "it's been a week" reminder is wrong for both.</para>
/// <para>It lives in Core, not in the banner, for the reason <see cref="Shopping.SpendForecast"/> does
/// (DESIGN.md §13.7): logic private to a page is logic no test can reach. It is also the ONE place that
/// decides — the banner renders an <see cref="ReceiptReminder"/> or renders nothing, and never
/// re-derives "is it due" from the gap beside it (CLAUDE.md's one-prediction-one-story rule).</para>
/// <para>Pure arithmetic over dates the caller supplies: no AI call, no credits, nothing metered.</para>
/// </summary>
public static class UploadCadence
{
    /// <summary>Distinct upload DAYS needed before a household has a rhythm worth measuring — four days
    /// is three completed gaps, enough for a median to mean something. Below it there is no reminder at
    /// all: a household three days into using the app has told us nothing about how often it shops, and
    /// guessing at a cadence in order to nag them is the worst first impression the feature could make.</summary>
    public const int MinimumUploadDays = 4;

    /// <summary>The "+ a day" of slack: a reminder fires one day AFTER the usual gap has passed, so a
    /// household that lands bang on its own rhythm is never nudged about a receipt it is about to
    /// upload anyway.</summary>
    public const int SlackDays = 1;

    /// <summary>A floor on the quiet stretch, whatever the learned gap says. A household that uploads
    /// most days has a median gap of 1, and a banner appearing every second day is nagging rather than
    /// reminding — the feature would get dismissed permanently within a week.</summary>
    public const int MinimumQuietDays = 3;

    /// <summary>Whether this household is overdue to upload, and the numbers behind that. Returns
    /// <c>null</c> when there is nothing to say — too little history to have a rhythm, still inside it,
    /// or dismissed and not yet back.
    /// <para><paramref name="uploadedAt"/> is every moment a receipt was uploaded, in any order; only
    /// the DAY of each matters, and a day is counted once however many receipts arrived on it. That is
    /// deliberate: three receipts carried in from one trip are one upload, and counting them as three
    /// zero-day gaps would drag the learned cadence toward zero and nag a household for being thorough.</para>
    /// <para><paramref name="snoozedUntil"/> is the household's last "Not now" (see
    /// <see cref="ReceiptReminder.SnoozeUntil"/>); null when they have never dismissed one.</para></summary>
    public static ReceiptReminder? Evaluate(
        IReadOnlyList<DateTimeOffset> uploadedAt, DateOnly today, DateOnly? snoozedUntil = null)
    {
        var days = uploadedAt
            .Select(u => DateOnly.FromDateTime(u.LocalDateTime))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (days.Count < MinimumUploadDays) return null;

        var last = days[^1];
        // Can go NEGATIVE, and deliberately has no guard of its own: a receipt stamped in the future (a
        // restored backup, a box whose clock was wrong) is a quiet stretch below zero, and the
        // remind-after test below — whose floor is never less than a day — already refuses it. A guard
        // here would be a branch no input can reach and no test can fail on.
        var daysSince = today.DayNumber - last.DayNumber;

        var gaps = new List<int>();
        for (var i = 1; i < days.Count; i++) gaps.Add(days[i].DayNumber - days[i - 1].DayNumber);

        // A MEDIAN, not the mean the feature was asked for in words. One fortnight away from home turns a
        // household's 4-day mean into a 6-day one and keeps it there for months; the median shrugs it off
        // and goes back to describing the ordinary week. The prediction engine reaches for the same
        // statistic for the same reason (ReplenishmentPredictor's rebuy rhythm) — but note this is a
        // DIFFERENT question over a different population (when the household uploads, not when one
        // product is rebought), so the two are deliberately not the same number and must not be merged.
        var usualGap = Median(gaps);
        var remindAfter = Math.Max(usualGap + SlackDays, MinimumQuietDays);
        if (daysSince < remindAfter) return null;

        // Dismissal is a snooze, not a mute: it buys one more of the household's own gaps. A permanent
        // "never show me this" belongs in Settings, where a household can see it is off and turn it back
        // on — a banner's × that silently retires a feature is a dead end nobody can find their way out of.
        if (snoozedUntil is { } until && today <= until) return null;

        return new ReceiptReminder(
            LastUploadedOn: last,
            DaysSinceLastUpload: daysSince,
            UsualGapDays: usualGap,
            RemindAfterDays: remindAfter,
            SnoozeUntil: today.AddDays(usualGap));
    }

    /// <summary>Median of a non-empty list of whole-day gaps, rounded away from zero on an even count so
    /// the result stays a whole number of days (the feature speaks in days; "every 6.5 days" is not a
    /// sentence a banner can say). Rounding UP on a tie waits the longer of the two middles, which is the
    /// side that under-nags.</summary>
    private static int Median(List<int> gaps)
    {
        // Stryker disable once Linq: `OrderBy` → `OrderByDescending` is unobservable — a median is
        // sort-direction invariant, the same reason ReplenishmentPredictor.Median carries this note.
        // Odd count: desc[mid] == asc[mid], since mid == count - 1 - mid. Even count: the two middles
        // swap places and the mean of the pair is unchanged.
        var sorted = gaps.OrderBy(g => g).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (int)Math.Round((sorted[mid - 1] + sorted[mid]) / 2.0, MidpointRounding.AwayFromZero);
    }
}

/// <summary>A household that is overdue to upload a receipt, measured against its OWN rhythm — the whole
/// story behind the banner, produced in one place so nothing beside the nudge can contradict it.</summary>
/// <param name="LastUploadedOn">The day the most recent receipt was uploaded.</param>
/// <param name="DaysSinceLastUpload">Whole days from that day to today.</param>
/// <param name="UsualGapDays">The household's learned median gap between upload days.</param>
/// <param name="RemindAfterDays">The quiet stretch that triggered this — the usual gap plus a day of
/// slack, never less than <see cref="UploadCadence.MinimumQuietDays"/>.</param>
/// <param name="SnoozeUntil">The day a "Not now" would hide the reminder through (inclusive). Computed
/// here rather than by the button, so what the dismissal does is one of the engine's own numbers.</param>
public sealed record ReceiptReminder(
    DateOnly LastUploadedOn,
    int DaysSinceLastUpload,
    int UsualGapDays,
    int RemindAfterDays,
    DateOnly SnoozeUntil);
