using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Diagnostics;

/// <summary>The error log's writes and reads over auth.db, separate from the background loop so
/// the dedupe and retention rules are directly testable. Dedupe: one row per fingerprint, counting
/// occurrences. Retention: the table is bounded at <see cref="MaxRows"/>, trimming the
/// longest-quiet rows first, so the log can never grow without limit on a small box.
///
/// Writes assume a SINGLE writer (the sink's channel is SingleReader and ErrorLogWriter is its
/// only consumer) — that is what makes check-then-insert safe against the unique fingerprint
/// index. Reads are served to pages only through AdminReportReader, which carries the admin gate;
/// nothing else should surface this data.</summary>
public sealed class ErrorLogStore(IDbContextFactory<AuthDbContext> dbFactory)
{
    public const int MaxRows = 500;

    /// <summary>The identity of a log SITE: category + exception type + message template. The
    /// rendered message is deliberately excluded (it varies per occurrence) — but stands in when
    /// no template exists, so unstructured log calls still group by their constant text.</summary>
    public static string FingerprintOf(CapturedError e) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{e.Category}\n{e.ExceptionType}\n{e.MessageTemplate ?? e.Message}")));

    public async Task RecordAsync(CapturedError e, CancellationToken ct = default)
    {
        var fingerprint = FingerprintOf(e);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var updated = await db.ErrorLog
            .Where(r => r.Fingerprint == fingerprint)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Count, r => r.Count + 1)
                .SetProperty(r => r.LastSeenAt, e.At)
                .SetProperty(r => r.LastMessage, e.Message)
                .SetProperty(r => r.ExceptionDetail, e.ExceptionDetail), ct);
        if (updated > 0) return;

        db.ErrorLog.Add(new ErrorLogEntry
        {
            Fingerprint = fingerprint,
            Level = e.Level,
            Category = e.Category,
            ExceptionType = e.ExceptionType,
            LastMessage = e.Message,
            ExceptionDetail = e.ExceptionDetail,
            Count = 1,
            FirstSeenAt = e.At,
            LastSeenAt = e.At,
        });
        await db.SaveChangesAsync(ct);

        // Only an insert can grow the table, so only an insert pays for the trim check.
        await TrimAsync(db, ct);
    }

    // SQLite refuses DateTimeOffset in a SQL ORDER BY, so both the trim's pick and the list's
    // ordering happen client-side — honest and cheap because the table is bounded at MaxRows.
    private static async Task TrimAsync(AuthDbContext db, CancellationToken ct)
    {
        var over = await db.ErrorLog.CountAsync(ct) - MaxRows;
        if (over <= 0) return;
        var stamps = await db.ErrorLog.Select(r => new { r.Id, r.LastSeenAt }).ToListAsync(ct);
        var oldest = stamps.OrderBy(r => r.LastSeenAt).ThenBy(r => r.Id)
            .Take(over).Select(r => r.Id).ToList();
        await db.ErrorLog.Where(r => oldest.Contains(r.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<List<ErrorLogEntry>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ErrorLog.AsNoTracking().ToListAsync(ct);
        return [.. rows.OrderByDescending(r => r.LastSeenAt).ThenByDescending(r => r.Id)];
    }

    /// <summary>Stamp (or, with null, clear) the admin's "handled" mark on one row. Column-scoped
    /// ExecuteUpdate like every write here; whether a stamped row COUNTS as resolved stays derived
    /// (<see cref="ErrorLogEntry.Resolved"/>), so <see cref="RecordAsync"/> never learns the field
    /// exists. Returns whether the row still exists — the trim can beat the admin to it.</summary>
    public async Task<bool> SetResolvedAsync(int id, DateTimeOffset? resolvedAt, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ErrorLog.Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ResolvedAt, resolvedAt), ct) > 0;
    }
}
