using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Ingest;
using ShelfAware.Core.Settings;

namespace ShelfAware.Web.Data;

/// <summary>
/// Ties <see cref="UploadCadence"/> — the pure arithmetic — to this household's upload history and its
/// "Not now". The ONE place the reminder is read and dismissed, so no second surface can decide for
/// itself whether a household is overdue (CLAUDE.md's one-definition rule).
/// <para>Costs nothing to ask: it is a date query and a settings read, with no AI call anywhere in it.
/// Nothing here is metered, and the banner it feeds must never look like something the household paid for.</para>
/// </summary>
public sealed class ReceiptReminderService(IHouseholdDbFactory dbFactory, IAppSettings settings)
{
    /// <summary>The household's reminder, or null when there is nothing to say. <paramref name="today"/>
    /// is passed in rather than read from the clock so the whole rule is testable at a date.</summary>
    public async Task<ReceiptReminder?> GetAsync(DateOnly today, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Every receipt the household has uploaded, whatever became of it — a failed read and a review
        // still sitting open are uploads too. Rows predating the column (UploadedAt null, no ConfirmedAt
        // to backfill from) contribute no day rather than a guessed one.
        var uploadedAt = await db.Receipts.AsNoTracking()
            .Where(r => r.UploadedAt != null)
            .Select(r => r.UploadedAt!.Value)
            .ToListAsync(ct);

        var snoozedUntil = ParseDay(await settings.GetAsync(SettingKeys.ReceiptReminderSnoozedUntil, ct));
        return UploadCadence.Evaluate(uploadedAt, today, snoozedUntil);
    }

    /// <summary>Hide the reminder through <see cref="ReceiptReminder.SnoozeUntil"/> — the engine's own
    /// number, not the button's, so "Not now" means exactly one more of this household's quiet
    /// stretches, floor included.</summary>
    public Task SnoozeAsync(ReceiptReminder reminder, CancellationToken ct = default) =>
        settings.SetAsync(
            SettingKeys.ReceiptReminderSnoozedUntil,
            reminder.SnoozeUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ct);

    /// <summary>A stored ISO day, or null for absent — and also for anything unparseable, which is the
    /// safe direction: a corrupt snooze value shows the reminder again rather than silently retiring the
    /// feature for a household that can never see why.</summary>
    private static DateOnly? ParseDay(string? stored) =>
        DateOnly.TryParseExact(stored, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
}
