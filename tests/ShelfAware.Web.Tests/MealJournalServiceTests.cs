using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Journal;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The meal journal's write path and — the part that earns its own suite — its privacy. Everything else in
/// the pantry is shared by the household; a journal is one person's. Real SQLite with the real query filter
/// and stamp, one TestDb re-pointed between members exactly like two people signed in to one household.
/// </summary>
public class MealJournalServiceTests : IDisposable
{
    private const string Jordan = "member-jordan";
    private const string Spouse = "member-spouse";

    private readonly TestDb _db = new() { MemberId = Jordan };

    public void Dispose() => _db.Dispose();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private MealJournalService As(string? member, string household = "hh-test")
    {
        _db.MemberId = member;
        _db.HouseholdId = household;
        return new MealJournalService(_db);
    }

    private static JournalDraft Draft(string food = "Turkey sandwich", int? calories = 450, bool estimated = true,
        MealSlot slot = MealSlot.Lunch, DateOnly? on = null) =>
        new(food, slot, on ?? Today, calories, estimated);

    private static DateSpan TodayOnly => new(Today, Today);

    // --- the write path -------------------------------------------------------------------------

    [Fact]
    public async Task A_logged_food_reads_back_trimmed_with_its_estimate_flag()
    {
        var journal = As(Jordan);

        var write = await journal.LogAsync(Draft(food: "  Turkey sandwich  "));

        Assert.Null(write.Problem);
        var entry = Assert.Single((await journal.GetAsync(TodayOnly)).Entries);
        Assert.Equal("Turkey sandwich", entry.Food);
        Assert.Equal(450, entry.Calories);
        Assert.True(entry.CaloriesEstimated);
        Assert.Equal(MealSlot.Lunch, entry.Slot);
        Assert.Equal(Jordan, entry.MemberId); // stamped by the context, never passed in
    }

    [Fact]
    public async Task An_entry_with_no_calories_is_never_marked_an_estimate()
    {
        // There is no number for the flag to describe; a stale "estimate" would put a ~ on a blank.
        var journal = As(Jordan);
        await journal.LogAsync(Draft(calories: null, estimated: true));

        Assert.False(Assert.Single((await journal.GetAsync(TodayOnly)).Entries).CaloriesEstimated);
    }

    [Fact]
    public async Task The_write_path_asks_the_rule_itself_so_no_caller_can_skip_it()
    {
        var journal = As(Jordan);

        var write = await journal.LogAsync(Draft(on: Today.AddDays(1)));

        Assert.Equal(MealJournal.Problem("x", 1, Today.AddDays(1), Today), write.Problem);
        Assert.Null(write.Entry);
        Assert.Empty((await journal.GetAsync(new DateSpan(Today, Today.AddDays(1)))).Entries);
    }

    [Fact]
    public async Task Reading_a_span_returns_only_its_days_in_eating_order()
    {
        var journal = As(Jordan);
        await journal.LogAsync(Draft(food: "Late", on: Today));
        await journal.LogAsync(Draft(food: "Outside", on: Today.AddDays(-5)));
        await journal.LogAsync(Draft(food: "Early", on: Today.AddDays(-1)));

        var read = (await journal.GetAsync(new DateSpan(Today.AddDays(-1), Today))).Entries;

        Assert.Equal(["Early", "Late"], read.Select(e => e.Food));
    }

    [Fact]
    public async Task A_correction_is_saved_and_a_bad_one_is_refused_not_clamped()
    {
        var journal = As(Jordan);
        var id = (await journal.LogAsync(Draft())).Entry!.Id;

        var fixedUp = await journal.UpdateAsync(id, Draft(food: "Half a sandwich", calories: 225, estimated: false, slot: MealSlot.Snack));
        var refused = await journal.UpdateAsync(id, Draft(calories: -5));

        Assert.Null(fixedUp.Problem);
        Assert.Equal("Calories can't be negative.", refused.Problem);
        var entry = Assert.Single((await journal.GetAsync(TodayOnly)).Entries);
        Assert.Equal(("Half a sandwich", 225, false, MealSlot.Snack), (entry.Food, entry.Calories, entry.CaloriesEstimated, entry.Slot));
    }

    [Fact]
    public async Task Correcting_or_removing_an_entry_that_is_gone_says_so()
    {
        var journal = As(Jordan);

        Assert.Equal("That entry is no longer in your journal.", (await journal.UpdateAsync(999, Draft())).Problem);
        Assert.False(await journal.DeleteAsync(999));
    }

    [Fact]
    public async Task Removing_an_entry_deletes_it()
    {
        var journal = As(Jordan);
        var id = (await journal.LogAsync(Draft())).Entry!.Id;

        Assert.True(await journal.DeleteAsync(id));
        Assert.Empty((await journal.GetAsync(TodayOnly)).Entries);
    }

    // --- privacy --------------------------------------------------------------------------------

    [Fact]
    public async Task One_members_journal_is_invisible_to_another_member_of_the_same_household()
    {
        await As(Jordan).LogAsync(Draft(food: "Jordan's lunch"));

        Assert.Empty((await As(Spouse).GetAsync(TodayOnly)).Entries);
        Assert.Equal("Jordan's lunch", Assert.Single((await As(Jordan).GetAsync(TodayOnly)).Entries).Food);
    }

    [Fact]
    public async Task Another_member_cannot_correct_or_remove_your_entries_by_id()
    {
        var id = (await As(Jordan).LogAsync(Draft(food: "Jordan's lunch"))).Entry!.Id;

        var spouse = As(Spouse);
        Assert.Equal("That entry is no longer in your journal.", (await spouse.UpdateAsync(id, Draft(food: "Edited"))).Problem);
        Assert.False(await spouse.DeleteAsync(id));

        Assert.Equal("Jordan's lunch", Assert.Single((await As(Jordan).GetAsync(TodayOnly)).Entries).Food);
    }

    [Fact]
    public async Task Another_household_sees_nothing_even_under_the_same_member_id()
    {
        // Belt and braces: the member filter narrows the household one, it does not replace it.
        await As(Jordan, household: "hh-a").LogAsync(Draft());

        Assert.Empty((await As(Jordan, household: "hh-b").GetAsync(TodayOnly)).Entries);
    }

    [Fact]
    public async Task A_scope_with_nobody_signed_in_is_refused_rather_than_read_as_empty_and_cannot_write()
    {
        await As(Jordan).LogAsync(Draft());

        var nobody = As(member: null);
        var read = await nobody.GetAsync(TodayOnly);
        Assert.Equal((0, MealJournalService.NobodySignedIn), (read.Entries.Count, read.Problem));
        var write = await nobody.LogAsync(Draft());
        Assert.Equal(MealJournalService.NobodySignedIn, write.Problem);
    }

    [Fact]
    public async Task The_context_refuses_a_journal_insert_with_no_member_even_past_the_service()
    {
        _db.MemberId = null;
        await using var db = _db.CreateDbContext();
        db.JournalEntries.Add(new JournalEntry { Food = "Toast", EatenOn = Today });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("knows no member", ex.Message);
    }

    [Fact]
    public async Task The_context_refuses_an_insert_naming_another_member()
    {
        _db.MemberId = Jordan;
        await using var db = _db.CreateDbContext();
        db.JournalEntries.Add(new JournalEntry { Food = "Toast", EatenOn = Today, MemberId = Spouse });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("another member", ex.Message);
    }

    [Fact]
    public async Task The_context_refuses_a_detached_update_or_delete_of_another_members_row()
    {
        // EF builds these from the primary key alone, so no filter ever sees them — the stamp-and-refuse is
        // the only thing between a forged id and someone else's journal.
        var id = (await As(Jordan).LogAsync(Draft())).Entry!.Id;
        _db.MemberId = Spouse;

        await using (var db = _db.CreateDbContext())
        {
            var forged = new JournalEntry { Id = id, HouseholdId = "hh-test", MemberId = Jordan, Food = "Hacked", EatenOn = Today };
            db.JournalEntries.Update(forged);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = _db.CreateDbContext())
        {
            db.JournalEntries.Remove(new JournalEntry { Id = id, HouseholdId = "hh-test", MemberId = Jordan, Food = "x", EatenOn = Today });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        Assert.Equal("Turkey sandwich", Assert.Single((await As(Jordan).GetAsync(TodayOnly)).Entries).Food);
    }

    [Fact]
    public async Task Household_tables_are_untouched_by_the_member_scope()
    {
        // The member narrows ONLY the per-person tables: a household product logged by one member is the
        // other member's too, exactly as before the journal existed.
        _db.MemberId = Jordan;
        await using (var db = _db.CreateDbContext())
        {
            db.Products.Add(new Product { Name = "Whole Milk", Category = Category.Dairy });
            await db.SaveChangesAsync();
        }
        _db.MemberId = Spouse;
        await using var spouse = _db.CreateDbContext();
        Assert.Single(await spouse.Products.ToListAsync());
    }
}
