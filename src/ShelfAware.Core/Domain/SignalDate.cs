namespace ShelfAware.Core.Domain;

/// <summary>
/// The calendar date a timestamped event happened on — the ONE reading, because two of them disagree
/// exactly when it matters least conveniently.
/// <para>Signals are written as <c>DateTimeOffset.Now</c>, and the whole app dates things in server-local
/// time deliberately (see the timezone deploy note in CLAUDE.md). <c>.Date</c> takes the date in the
/// offset the event was RECORDED with, so a signal keeps the day it happened on forever.
/// <c>.LocalDateTime.Date</c> re-reads it in whatever zone the machine is in NOW, which silently shifts
/// historical rows by a day if a deployment ever moves timezone — the documented UTC-cloud-box <c>TZ</c>
/// gotcha. The predictor uses the first, so everything else must too: a signal that pairs into a burn
/// cycle in the engine but reads a day later in a report is two screens disagreeing about the same fact.</para>
/// </summary>
public static class SignalDate
{
    public static DateOnly Of(DateTimeOffset at) => DateOnly.FromDateTime(at.Date);
}
