using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

public sealed class ActivityLogOptions
{
    public const string SectionName = "ActivityLog";

    /// <summary>Rows kept per household; null = unbounded (the self-host default). A shared box sets this
    /// so the log can't grow without limit — the oldest are trimmed first, like <c>ErrorLogStore</c>.
    /// ⚠️ Trimming drops an old entry's undo, which is acceptable for old actions.</summary>
    public int? MaxRows { get; set; }
}

/// <summary>The recording seam the data layer depends on — one method, so <c>EfPantryStore</c> and the
/// confirm/edit services log an action without knowing how undo works.</summary>
public interface IActivityLog
{
    /// <summary>Record an undoable action AFTER it has committed. Best-effort: returns null (logged as a
    /// warning) rather than throwing on a store failure — a logged action really happened, and an
    /// unlogged one merely can't be undone from history. Throws only on a programmer error (recording a
    /// kind with no registered handler).</summary>
    Task<RecordedActivity?> RecordAsync(
        ActivityKind kind, object payload, string? source = null, CancellationToken cancellationToken = default);
}

/// <summary>A freshly recorded entry's id and stored summary — enough for an inline "↩ Undo" affordance.</summary>
public sealed record RecordedActivity(int Id, string Summary, Reversibility Reversibility);

/// <summary>Typed result of an undo attempt, for the UI to word (item 27: advice splits by what happened).</summary>
public enum UndoOutcome { Done, AlreadyUndone, Superseded, Gone, NotReversible }

/// <summary>The backbone of the activity log + undo. ONE service backs both surfaces — the inline
/// "↩ Undo" and the /history page call the same <see cref="UndoAsync"/>, never a per-surface copy of
/// the reversal (CLAUDE.md's "one definition" rule). Scoped, because every read and write goes through
/// <see cref="IHouseholdDbFactory"/>, which needs the scope's signed-in household.
///
/// ⚠️ This is the app's SECOND cross-row write-orchestration (after <c>ReceiptRemovalService</c>). It
/// NEVER uses <c>IgnoreQueryFilters</c>: the entry is loaded through the household-filtered context and
/// every row a handler deletes or edits is reached through that same context, so <c>EnforceHousehold</c>
/// already refuses a cross-household write and an undo cannot touch another household's data — pinned by
/// the isolation test (household B cannot undo household A's entry).</summary>
public sealed class ActivityLogService : IActivityLog
{
    private readonly IHouseholdDbFactory _dbFactory;
    private readonly IReadOnlyDictionary<ActivityKind, IUndoHandler> _handlers;
    private readonly int? _maxRows;
    private readonly ILogger<ActivityLogService> _logger;

    public ActivityLogService(
        IHouseholdDbFactory dbFactory,
        IEnumerable<IUndoHandler> handlers,
        IOptions<ActivityLogOptions> options,
        ILogger<ActivityLogService> logger)
    {
        _dbFactory = dbFactory;
        _handlers = handlers.ToDictionary(h => h.Kind);
        _maxRows = options.Value.MaxRows;
        _logger = logger;
    }

    public async Task<RecordedActivity?> RecordAsync(
        ActivityKind kind, object payload, string? source = null, CancellationToken cancellationToken = default)
    {
        if (!_handlers.TryGetValue(kind, out var handler))
            throw new InvalidOperationException(
                $"No IUndoHandler registered for {kind} — add one before recording that action.");

        var json = JsonSerializer.Serialize(payload, payload.GetType());
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var entry = new ActivityEntry
            {
                OccurredAt = DateTimeOffset.Now,
                Kind = kind,
                Summary = handler.Summarize(json),
                Reversibility = handler.Reversibility,
                PayloadJson = json,
                Source = source,
            };
            db.ActivityEntries.Add(entry);
            await db.SaveChangesAsync(cancellationToken);
            await TrimAsync(db, cancellationToken); // only an insert grows the table, so only an insert trims
            return new RecordedActivity(entry.Id, entry.Summary, entry.Reversibility);
        }
        catch (Exception ex) when (ex is DbUpdateException or SqliteException)
        {
            _logger.LogWarning(ex,
                "Couldn't record a {Kind} activity entry; the action stands, without an undo record.", kind);
            return null;
        }
    }

    /// <summary>Reverse one entry through its kind's handler — the ONE undo path for both surfaces. Loads
    /// the entry household-filtered (so a foreign id is indistinguishable from a missing one → Gone),
    /// refuses an already-undone or history-only entry, then lets the handler re-read state and decide.
    /// On success the handler's staged reversal AND the UndoneAt stamp commit in one SaveChanges; on a
    /// refusal nothing is saved.</summary>
    public async Task<UndoOutcome> UndoAsync(int entryId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.ActivityEntries.FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (entry is null) return UndoOutcome.Gone;                        // gone, or another household's
        if (entry.UndoneAt is not null) return UndoOutcome.AlreadyUndone;
        if (entry.Reversibility == Reversibility.NotReversible) return UndoOutcome.NotReversible;
        if (!_handlers.TryGetValue(entry.Kind, out var handler)) return UndoOutcome.NotReversible;

        var result = await handler.UndoAsync(db, entry, cancellationToken);
        if (result != UndoResult.Done)
            return result switch
            {
                UndoResult.Superseded => UndoOutcome.Superseded,
                UndoResult.Gone => UndoOutcome.Gone,
                _ => UndoOutcome.NotReversible,
            };

        entry.UndoneAt = DateTimeOffset.Now;
        await db.SaveChangesAsync(cancellationToken); // the handler's reversal + this stamp, one transaction
        return UndoOutcome.Done;
    }

    /// <summary>The household's actions, newest first. Ordered by Id — insert order IS chronological, and
    /// SQLite refuses <c>DateTimeOffset</c> in a SQL ORDER BY (item 47), so ordering by the int PK keeps
    /// paging in the database. <paramref name="take"/> null = the whole retained log.</summary>
    public async Task<IReadOnlyList<ActivityEntry>> GetHistoryAsync(
        int? take = null, int skip = 0, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<ActivityEntry> query = db.ActivityEntries.AsNoTracking().OrderByDescending(e => e.Id).Skip(skip);
        if (take is { } n) query = query.Take(n);
        return await query.ToListAsync(cancellationToken);
    }

    private async Task TrimAsync(ShelfAwareDbContext db, CancellationToken cancellationToken)
    {
        if (_maxRows is not { } max) return; // unbounded (self-host default)
        var over = await db.ActivityEntries.CountAsync(cancellationToken) - max;
        if (over <= 0) return;
        // Oldest by Id (insert order = chronological); Id is an int, so this ORDER BY is legal in SQL.
        var oldest = await db.ActivityEntries
            .OrderBy(e => e.Id).Take(over).Select(e => e.Id).ToListAsync(cancellationToken);
        await db.ActivityEntries.Where(e => oldest.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
