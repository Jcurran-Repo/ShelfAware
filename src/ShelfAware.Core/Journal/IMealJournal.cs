using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Journal;

/// <summary>
/// The meal journal's data port — what Reginald's journal tools act through. Defined in Core and implemented
/// in Web (the same split as <c>IPantryStore</c>), so the chat layer touches no EF. Separate from the pantry
/// store because the journal belongs to a PERSON, not the household: an implementation answers for whoever
/// is signed in, and refuses (<see cref="JournalWrite.Problem"/>) when nobody is.
/// </summary>
public interface IMealJournal
{
    /// <summary>Record one food. The write path checks <see cref="MealJournal.Problem"/> itself, so no caller
    /// can skip the rule; a refusal comes back as text to relay, not an exception.</summary>
    Task<JournalWrite> LogAsync(JournalDraft draft, CancellationToken cancellationToken = default);

    /// <summary>The signed-in person's entries eaten inside <paramref name="span"/> — or, when nobody is signed
    /// in, a <see cref="JournalRead.Problem"/>, so "nothing logged" is never said about a journal no one could
    /// see.</summary>
    Task<JournalRead> GetAsync(DateSpan span, CancellationToken cancellationToken = default);

    /// <summary>The foods the signed-in person has given their own calorie number for
    /// (<see cref="MealJournal.Remembered"/>) — empty when nobody is signed in. <see cref="LogAsync"/> applies
    /// them itself; this is so Reginald can name a food the way the person's journal already does.</summary>
    Task<IReadOnlyList<RememberedCalories>> RememberedAsync(CancellationToken cancellationToken = default);
}

/// <summary>An entry as asked for, before it is written.</summary>
/// <param name="CaloriesEstimated">True when <paramref name="Calories"/> is an estimate rather than a number
/// the person stated or typed.</param>
public sealed record JournalDraft(string Food, MealSlot Slot, DateOnly EatenOn, int? Calories, bool CaloriesEstimated)
{
    /// <summary>True when the draft carries a number the person gave — the draft's side of
    /// <see cref="MealJournal.IsOwnNumber"/>, and what <see cref="MealJournal.Recall"/> never overrides.</summary>
    public bool IsOwnNumber => Calories is not null && !CaloriesEstimated;
}

/// <summary>The outcome of a journal write: the saved entry, or why it wasn't saved.</summary>
/// <param name="Recalled">True when the entry's calories are the person's remembered number for that food
/// (<see cref="MealJournal.Recall"/>) rather than the number the draft came with.</param>
public sealed record JournalWrite(JournalEntry? Entry, string? Problem, bool Recalled = false)
{
    public static JournalWrite Saved(JournalEntry entry, bool recalled = false) => new(entry, null, recalled);
    public static JournalWrite Refused(string problem) => new(null, problem);
}

/// <summary>The outcome of a journal read: the person's entries, or why there is no person to read for.</summary>
public sealed record JournalRead(IReadOnlyList<JournalEntry> Entries, string? Problem)
{
    public static JournalRead Of(IReadOnlyList<JournalEntry> entries) => new(entries, null);
    public static JournalRead Refused(string problem) => new([], problem);
}
