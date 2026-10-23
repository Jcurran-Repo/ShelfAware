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

    /// <summary>Record one food. A draft without the person's own number takes the one they last gave for that
    /// food (<see cref="MealJournal.Recall"/>) — here, on the write path, so Reginald and the page's add form
    /// cannot disagree about whether a correction is remembered.</summary>
    public async Task<JournalWrite> LogAsync(JournalDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (db.MemberId is null) return JournalWrite.Refused(NobodySignedIn);

        var (written, recalled) = MealJournal.Recall(draft, await RememberedAsync(db, cancellationToken));
        // An estimate that prices a bigger size below a smaller one already logged (or the reverse) is wrong
        // on its face — refused here, on the write path, so no surface can store one.
        if (MealJournal.SizeConflict(written, await SizedAsync(db, except: null, cancellationToken)) is { } other)
            return JournalWrite.Refused(MealJournal.SizeConflictProblem(written, other));
        // A recalled number was given before, not now: it carries no stamp, so it can never outrank the
        // correction it came from, nor rename the food that correction is remembered by.
        var given = !recalled && MealJournal.GivesNumber(before: null, written);
        return await AddAsync(db, written, given ? DateTimeOffset.Now : null, recalled, remembered: given,
            cancellationToken);
    }

    /// <summary>Put a removed entry back exactly as it was — the page's undo — including when its number was
    /// given. No recall and no fresh stamp: an undo that came back with a different number, or that made a
    /// stale number the remembered one, would not be an undo.</summary>
    public async Task<JournalWrite> RestoreAsync(JournalEntry removed, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (db.MemberId is null) return JournalWrite.Refused(NobodySignedIn);

        var draft = new JournalDraft(removed.Food, removed.Slot, removed.EatenOn, removed.Calories, removed.CaloriesEstimated);
        return await AddAsync(db, draft, removed.CaloriesGivenAt, recalled: false, remembered: false, cancellationToken);
    }

    public async Task<IReadOnlyList<RememberedCalories>> RememberedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (db.MemberId is null) return [];
        return await RememberedAsync(db, cancellationToken);
    }

    private static async Task<IReadOnlyList<RememberedCalories>> RememberedAsync(
        ShelfAwareDbContext db, CancellationToken cancellationToken) =>
        MealJournal.Remembered(await db.JournalEntries
            .AsNoTracking()
            .Where(MealJournal.HasOwnNumber)
            .ToListAsync(cancellationToken));

    /// <summary>The entries a size can be compared against — ones with a number whose name may state ounces.
    /// The LIKE only narrows the read; <see cref="MealJournal.SizeConflict"/> parses the sizes itself.</summary>
    /// <param name="except">The entry being corrected, left out — its old size is what the edit replaces, not
    /// a second cup to compare with.</param>
    private static async Task<List<JournalEntry>> SizedAsync(ShelfAwareDbContext db, int? except,
        CancellationToken cancellationToken) =>
        await db.JournalEntries
            .AsNoTracking()
            .Where(e => e.Id != except
                && e.Calories != null && (EF.Functions.Like(e.Food, "%oz%") || EF.Functions.Like(e.Food, "%ounce%")))
            .ToListAsync(cancellationToken);

    /// <summary>The one insert. The rule is asked here, on what is actually written — after a recall has
    /// swapped in a remembered number, so a stored number cannot slip past a bound tightened since.</summary>
    private static async Task<JournalWrite> AddAsync(ShelfAwareDbContext db, JournalDraft draft,
        DateTimeOffset? caloriesGivenAt, bool recalled, bool remembered, CancellationToken cancellationToken)
    {
        if (MealJournal.Problem(draft.Food, draft.Calories, draft.EatenOn, Today) is { } problem)
            return JournalWrite.Refused(problem);

        var entry = new JournalEntry
        {
            Food = draft.Food.Trim(),
            Slot = draft.Slot,
            EatenOn = draft.EatenOn,
            Calories = draft.Calories,
            CaloriesEstimated = draft.Calories is not null && draft.CaloriesEstimated,
            CaloriesGivenAt = draft.IsOwnNumber ? caloriesGivenAt : null,
            LoggedAt = DateTimeOffset.Now,
        };
        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return JournalWrite.Saved(entry, recalled, remembered);
    }

    public async Task<JournalRead> GetAsync(DateSpan span, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (db.MemberId is null) return JournalRead.Refused(NobodySignedIn);
        return JournalRead.Of(await db.JournalEntries
            .AsNoTracking()
            .Where(e => e.EatenOn >= span.From && e.EatenOn <= span.To)
            // Day, then the order the rows were written — the id, because SQLite cannot ORDER BY a
            // DateTimeOffset, and an id is the write order LoggedAt would give anyway.
            .OrderBy(e => e.EatenOn).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken));
    }

    public async Task<JournalRead> GetEntryAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (db.MemberId is null) return JournalRead.Refused(NobodySignedIn);
        // The member filter applies to this read like every other: another person's id finds nothing.
        return JournalRead.Of(await db.JournalEntries.AsNoTracking().Where(e => e.Id == id).ToListAsync(cancellationToken));
    }

    /// <summary>Correct an entry. Refused, not clamped, when the correction breaks the rule — this is a
    /// number someone typed on purpose. Gone (deleted elsewhere, or not this person's) is a refusal too, and so
    /// is an estimate that prices a size against the journal's other sizes impossibly — the check
    /// <see cref="LogAsync"/> makes, because Reginald's <c>edit_meal</c> writes estimates through here.</summary>
    public async Task<JournalWrite> UpdateAsync(int id, JournalDraft draft, CancellationToken cancellationToken = default)
    {
        if (MealJournal.Problem(draft.Food, draft.Calories, draft.EatenOn, Today) is { } problem)
            return JournalWrite.Refused(problem);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.JournalEntries.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null) return JournalWrite.Refused("That entry is no longer in your journal.");
        if (MealJournal.SizeConflict(draft, await SizedAsync(db, except: id, cancellationToken)) is { } other)
            return JournalWrite.Refused(MealJournal.SizeConflictProblem(draft, other));

        // Asked before the fields move: whether this edit gives the food a number is a comparison with what
        // the entry said until now.
        var given = MealJournal.GivesNumber(entry, draft);
        entry.Food = draft.Food.Trim();
        entry.Slot = draft.Slot;
        entry.EatenOn = draft.EatenOn;
        entry.Calories = draft.Calories;
        entry.CaloriesEstimated = draft.Calories is not null && draft.CaloriesEstimated;
        // A number they just gave is stamped now — the newest given, so the one remembered. One they left as
        // it was keeps its stamp, and a number that is no longer theirs has none.
        if (given) entry.CaloriesGivenAt = DateTimeOffset.Now;
        else if (!draft.IsOwnNumber) entry.CaloriesGivenAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return JournalWrite.Saved(entry, remembered: given);
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
