using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

/// <summary>The recording seam the data layer depends on. Recording is ATOMIC with the action: the entry
/// is staged on the action's OWN context (and transaction), so it commits with the action — a committed
/// action always has its undo record, and a rolled-back action leaves none. Retention (<see
/// cref="TrimAsync"/>) is the separable cleanup half — best-effort, after the commit — so it can never
/// roll back or block a real action.</summary>
public interface IActivityLog
{
    /// <summary>Stage the undo record on the CALLER's context, to be committed by the caller's
    /// SaveChanges/transaction together with the action. Returns the (unsaved) entry — read its
    /// <see cref="ActivityEntry.Id"/> after the save for an inline undo affordance. Does NOT save.
    /// Throws only on a programmer error (recording a kind with no registered handler); being inside the
    /// action's transaction, that fails the action loudly rather than shipping a silently-broken log.</summary>
    ActivityEntry Record(ShelfAwareDbContext db, ActivityKind kind, object payload, string? source = null);

    /// <summary>Best-effort retention: trim the oldest rows past MaxRows for the current household. A
    /// no-op (no query, no context) when unbounded. Called AFTER the action commits and swallows its own
    /// errors — cleanup must never fail the action it follows.</summary>
    Task TrimAsync(CancellationToken cancellationToken = default);
}

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

    public ActivityEntry Record(ShelfAwareDbContext db, ActivityKind kind, object payload, string? source = null)
    {
        if (!_handlers.TryGetValue(kind, out var handler))
            throw new InvalidOperationException(
                $"No IUndoHandler registered for {kind} — add one before recording that action.");

        var json = JsonSerializer.Serialize(payload, payload.GetType());
        var entry = new ActivityEntry
        {
            OccurredAt = DateTimeOffset.Now,
            Kind = kind,
            Summary = handler.Summarize(json),
            Reversibility = handler.Reversibility,
            PayloadJson = json,
            Source = source,
        };
        db.ActivityEntries.Add(entry); // staged on the caller's context — the caller's SaveChanges commits it
        return entry;
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

    public async Task TrimAsync(CancellationToken cancellationToken = default)
    {
        if (_maxRows is not { } max) return; // unbounded (self-host default): no context, no cost
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var over = await db.ActivityEntries.CountAsync(cancellationToken) - max;
            if (over <= 0) return;
            // Oldest by Id (insert order = chronological); Id is an int, so this ORDER BY is legal in SQL.
            var oldest = await db.ActivityEntries
                .OrderBy(e => e.Id).Take(over).Select(e => e.Id).ToListAsync(cancellationToken);
            await db.ActivityEntries.Where(e => oldest.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            _logger.LogWarning(ex, "Couldn't trim the activity log; it may briefly exceed MaxRows.");
        }
    }
}
