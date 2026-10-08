using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Journal;

namespace ShelfAware.Web.Data;

/// <summary>
/// THE write path for the meal journal — Reginald's <c>log_meal</c> tool (through <see cref="IMealJournal"/>)
/// and the journal page's add, edit and delete all come through here, so <see cref="MealJournal.Problem"/>
/// is asked on every road in and none can skip it.
///
/// <para>Whose journal is decided by the context, not by this class: <see cref="IHouseholdDbFactory"/> hands
/// out contexts scoped to the signed-in person, and the query filter and insert stamp do the rest. Nothing
/// here takes a member id, so nothing here can be handed the wrong one.</para>
/// </summary>
public sealed class MealJournalService(IHouseholdDbFactory dbFactory) : IMealJournal
{
    /// <summary>What a write says when the scope has no person to own the entry.</summary>
    internal const string NobodySignedIn =
        "The meal journal needs someone signed in — it keeps each person's meals separately.";

    public async Task<JournalWrite> LogAsync(JournalDraft draft, CancellationToken cancellationToken = default)
    {
        if (MealJournal.Problem(draft.Food, draft.Calories, draft.EatenOn, Today) is { } problem)
            return JournalWrite.Refused(problem);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (db.MemberId is null) return JournalWrite.Refused(NobodySignedIn);

        var entry = new JournalEntry
        {
            Food = draft.Food.Trim(),
            Slot = draft.Slot,
            EatenOn = draft.EatenOn,
            Calories = draft.Calories,
            CaloriesEstimated = draft.Calories is not null && draft.CaloriesEstimated,
            LoggedAt = DateTimeOffset.Now,
        };
        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return JournalWrite.Saved(entry);
    }

    public async Task<IReadOnlyList<JournalEntry>> GetAsync(DateSpan span, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.JournalEntries
            .AsNoTracking()
            .Where(e => e.EatenOn >= span.From && e.EatenOn <= span.To)
            // Day, then the order the rows were written — the id, because SQLite cannot ORDER BY a
            // DateTimeOffset, and an id is the write order LoggedAt would give anyway.
            .OrderBy(e => e.EatenOn).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Correct an entry. Refused, not clamped, when the correction breaks the rule — this is a
    /// number someone typed on purpose. Gone (deleted elsewhere, or not this person's) is a refusal too.</summary>
    public async Task<JournalWrite> UpdateAsync(int id, JournalDraft draft, CancellationToken cancellationToken = default)
    {
        if (MealJournal.Problem(draft.Food, draft.Calories, draft.EatenOn, Today) is { } problem)
            return JournalWrite.Refused(problem);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.JournalEntries.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null) return JournalWrite.Refused("That entry is no longer in your journal.");

        entry.Food = draft.Food.Trim();
        entry.Slot = draft.Slot;
        entry.EatenOn = draft.EatenOn;
        entry.Calories = draft.Calories;
        entry.CaloriesEstimated = draft.Calories is not null && draft.CaloriesEstimated;
        await db.SaveChangesAsync(cancellationToken);
        return JournalWrite.Saved(entry);
    }

    /// <summary>Remove an entry. False when it was already gone (or was never this person's).</summary>
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // ExecuteDelete goes through the query filter, so another member's id deletes nothing.
        return await db.JournalEntries.Where(e => e.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
}
