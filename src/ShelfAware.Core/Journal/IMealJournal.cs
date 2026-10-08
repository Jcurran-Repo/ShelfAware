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

    /// <summary>The signed-in person's entries eaten inside <paramref name="span"/> (empty when nobody is
    /// signed in).</summary>
    Task<IReadOnlyList<JournalEntry>> GetAsync(DateSpan span, CancellationToken cancellationToken = default);
}

/// <summary>An entry as asked for, before it is written.</summary>
/// <param name="CaloriesEstimated">True when <paramref name="Calories"/> is an estimate rather than a number
/// the person stated or typed.</param>
public sealed record JournalDraft(string Food, MealSlot Slot, DateOnly EatenOn, int? Calories, bool CaloriesEstimated);

/// <summary>The outcome of a journal write: the saved entry, or why it wasn't saved.</summary>
public sealed record JournalWrite(JournalEntry? Entry, string? Problem)
{
    public static JournalWrite Saved(JournalEntry entry) => new(entry, null);
    public static JournalWrite Refused(string problem) => new(null, problem);
}
