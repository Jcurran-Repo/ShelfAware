using ShelfAware.Core.Domain;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The seam between the pure cadence (<c>UploadCadence</c>, tested over dates in ShelfAware.Tests) and
/// this household's own receipts: which rows count as an upload, that another household's receipts are
/// invisible, and that a "Not now" round-trips through the settings table. Real SQLite, so the v3 query
/// filter doing the tenancy work is the one production runs.
/// </summary>
public class ReceiptReminderServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ReceiptReminderService _service;

    public ReceiptReminderServiceTests()
        => _service = new ReceiptReminderService(_db, new EfAppSettings(_db));

    public void Dispose() => _db.Dispose();

    private static readonly DateOnly Day0 = new(2026, 3, 1);

    private static DateOnly D(int d) => Day0.AddDays(d);

    /// <summary>Confirmed receipts uploaded on the given days — a household with a rhythm.</summary>
    private async Task UploadedOn(params int[] days)
    {
        await using var db = _db.CreateDbContext();
        foreach (var day in days)
        {
            db.Receipts.Add(new Receipt
            {
                ImagePath = $"receipt-{day}",
                Status = ReceiptStatus.Confirmed,
                PurchasedAt = D(day),
                UploadedAt = new DateTimeOffset(D(day).ToDateTime(new TimeOnly(15, 0))),
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_household_past_its_own_gap_gets_a_reminder()
    {
        await UploadedOn(0, 7, 14, 21);

        Assert.Null(await _service.GetAsync(D(28)));   // still inside the weekly rhythm

        var reminder = await _service.GetAsync(D(29));
        Assert.NotNull(reminder);
        Assert.Equal(7, reminder.UsualGapDays);
        Assert.Equal(8, reminder.DaysSinceLastUpload);
    }

    [Fact]
    public async Task A_receipt_still_sitting_in_review_counts_as_an_upload()
    {
        // The reminder asks "have you uploaded lately?", not "have you finished reviewing?". Nudging
        // someone to upload a receipt that is at that moment open on their review screen is the one
        // thing it must never do — which is why the stamp is at row creation, not at confirm.
        await UploadedOn(0, 7, 14);
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt
            {
                ImagePath = "pending",
                Status = ReceiptStatus.PendingReview,
                UploadedAt = new DateTimeOffset(D(21).ToDateTime(new TimeOnly(15, 0))),
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await _service.GetAsync(D(28)));
        Assert.Equal(D(21), (await _service.GetAsync(D(29)))!.LastUploadedOn);
    }

    [Fact]
    public async Task A_receipt_from_before_the_column_existed_contributes_no_upload_day()
    {
        // UploadedAt null and nothing to backfill from: the cadence is taken over the days it can
        // actually see, and four such days are what it takes to have one at all.
        await UploadedOn(0, 7, 14, 21);
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt { ImagePath = "ancient", Status = ReceiptStatus.Confirmed, PurchasedAt = D(28) });
            await db.SaveChangesAsync();
        }

        // The day-28 receipt is invisible to the rhythm, so the last upload is still day 21.
        var reminder = await _service.GetAsync(D(29));
        Assert.NotNull(reminder);
        Assert.Equal(D(21), reminder.LastUploadedOn);
    }

    [Fact]
    public async Task Another_households_receipts_are_not_part_of_this_households_rhythm()
    {
        await UploadedOn(0, 7, 14, 21);

        _db.HouseholdId = "hh-other";
        // The neighbour uploads constantly and has uploaded today. Neither their rhythm nor their
        // recency may reach into this household's reminder — the query filter is what holds that,
        // and this is the pin on it.
        await UploadedOn(25, 26, 27, 28, 29);
        Assert.Null(await _service.GetAsync(D(29)));   // the neighbour uploaded today: nothing to say

        _db.HouseholdId = "hh-test";
        var reminder = await _service.GetAsync(D(29));
        Assert.NotNull(reminder);
        Assert.Equal(D(21), reminder.LastUploadedOn);
        Assert.Equal(7, reminder.UsualGapDays);
    }

    [Fact]
    public async Task Not_now_snoozes_for_one_more_of_the_households_own_gaps()
    {
        await UploadedOn(0, 7, 14, 21);
        var reminder = await _service.GetAsync(D(29));
        Assert.NotNull(reminder);

        await _service.SnoozeAsync(reminder);

        // Stored as the engine's own SnoozeUntil, in a form that reads back the same anywhere.
        var settings = new EfAppSettings(_db);
        Assert.Equal("2026-04-06", await settings.GetAsync(SettingKeys.ReceiptReminderSnoozedUntil));

        Assert.Null(await _service.GetAsync(D(36)));      // hidden through the snooze day itself
        Assert.NotNull(await _service.GetAsync(D(37)));   // and back the morning after
    }

    [Fact]
    public async Task An_unreadable_snooze_shows_the_reminder_rather_than_retiring_it()
    {
        // A hand-edited or half-written settings row must not mute a feature for good, with nothing in
        // the app that could ever bring it back.
        await UploadedOn(0, 7, 14, 21);
        await new EfAppSettings(_db).SetAsync(SettingKeys.ReceiptReminderSnoozedUntil, "soon");

        Assert.NotNull(await _service.GetAsync(D(29)));
    }

    [Fact]
    public async Task A_household_with_no_receipts_at_all_is_left_alone()
    {
        Assert.Null(await _service.GetAsync(D(29)));
    }
}
