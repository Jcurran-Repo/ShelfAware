using System.Text.Json;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>The outcome of trying to reverse one entry.</summary>
public enum UndoResult
{
    /// <summary>Reversed. The handler has staged its changes on the caller's context; the caller commits
    /// them together with the <see cref="ActivityEntry.UndoneAt"/> stamp.</summary>
    Done,

    /// <summary>A later action changed the same thing, so reversing now would clobber it. Refuse.</summary>
    Superseded,

    /// <summary>What the entry reversed is already gone (deleted by another path). Nothing to do.</summary>
    Gone,

    /// <summary>This kind has no clean inverse (history-only). Never reached for a Reversible kind.</summary>
    NotReversible,
}

/// <summary>One per <see cref="ActivityKind"/>. Owns that kind's payload shape, produces its
/// <see cref="ActivityEntry.Summary"/> at record time, declares its <see cref="Reversibility"/>, and
/// reverses it — precondition-checked, NEVER blind.
///
/// The load-bearing rule (CLAUDE.md's signature failure is two places disagreeing about one fact, and a
/// blind undo is exactly that across time): <see cref="UndoAsync"/> re-reads current state on the passed
/// context and reverses ONLY if that state still matches what the entry recorded, otherwise returns
/// <see cref="UndoResult.Superseded"/>/<see cref="UndoResult.Gone"/>. It STAGES its changes on
/// <c>db</c> but does NOT SaveChanges — <c>ActivityLogService</c> commits the reversal and the
/// <see cref="ActivityEntry.UndoneAt"/> stamp in one transaction, so a refused undo leaves nothing
/// half-written. This is the same discipline as <c>ReceiptRemovalService</c> (reverse by stored
/// provenance, keep what gained other history) and <c>MealStock</c> (item 28).</summary>
public interface IUndoHandler
{
    ActivityKind Kind { get; }
    Reversibility Reversibility { get; }

    /// <summary>The human line for a fresh entry of this kind, from its payload. Stored on the entry.</summary>
    string Summarize(string payloadJson);

    Task<UndoResult> UndoAsync(ShelfAwareDbContext db, ActivityEntry entry, CancellationToken cancellationToken);
}

/// <summary>A handler whose reversal has a side-effect that must run AFTER the DB commit AND must NEVER run
/// during a Peek — the receipt-confirm undo deletes the receipt's image folder, a filesystem op that can't
/// be staged on the context (and so can't roll back with a refused undo) and that a Peek must not perform
/// (⚠️ Peek re-runs <see cref="IUndoHandler.UndoAsync"/> to test undoability, then discards — a Peek that
/// deleted the image would destroy it just by rendering the /history row). Implemented ALONGSIDE
/// <see cref="UndoHandler{TPayload}"/>; <c>ActivityLogService.UndoAsync</c> calls this only on a real,
/// committed undo, never <c>PeekAsync</c>. Best-effort by contract: the DB reversal has already committed,
/// so this must not throw back into the undo (an orphaned image folder beats a failed undo, and "delete my
/// data" still reaches it).</summary>
public interface IUndoAfterCommit
{
    Task AfterCommitAsync(ActivityEntry entry, CancellationToken cancellationToken);
}

/// <summary>Typed base: a handler works with its own payload record and lets this class own the JSON
/// (de)serialization at the boundary. Reversible by default; a history-only handler overrides
/// <see cref="Reversibility"/>, and its <see cref="Reverse"/> is never reached (the service refuses a
/// NotReversible kind before dispatching).</summary>
public abstract class UndoHandler<TPayload> : IUndoHandler
{
    public abstract ActivityKind Kind { get; }
    public virtual Reversibility Reversibility => Reversibility.Reversible;

    string IUndoHandler.Summarize(string payloadJson) => Summarize(Deserialize(payloadJson));

    Task<UndoResult> IUndoHandler.UndoAsync(ShelfAwareDbContext db, ActivityEntry entry, CancellationToken ct) =>
        Reverse(db, Deserialize(entry.PayloadJson), entry, ct);

    /// <summary>The stored summary line for a payload.</summary>
    protected abstract string Summarize(TPayload payload);

    /// <summary>Re-read state on <paramref name="db"/>, and reverse only if it still matches
    /// <paramref name="payload"/>; stage changes but do NOT save. <paramref name="entry"/> carries
    /// <see cref="ActivityEntry.OccurredAt"/> for time-based precondition checks.</summary>
    protected abstract Task<UndoResult> Reverse(
        ShelfAwareDbContext db, TPayload payload, ActivityEntry entry, CancellationToken ct);

    private static TPayload Deserialize(string json) =>
        JsonSerializer.Deserialize<TPayload>(json) ?? throw new InvalidOperationException(
            $"Activity payload for {typeof(TPayload).Name} deserialized to null: {json}");
}

/// <summary>A handler for an action that is RECORDED but has no clean inverse — merge, census-confirm,
/// receipt-removal (DESIGN.md's "hard to reverse → history-only for v1"). It declares
/// <see cref="Reversibility.NotReversible"/>, so <c>ActivityLogService</c> refuses the undo (greyed on
/// /history) before ever dispatching, and its <see cref="UndoHandler{TPayload}.Reverse"/> is never reached
/// — implemented here once, so a concrete history-only handler only writes its <see cref="Kind"/> and its
/// summary and cannot accidentally ship as reversible.</summary>
public abstract class HistoryOnlyHandler<TPayload> : UndoHandler<TPayload>
{
    public sealed override Reversibility Reversibility => Reversibility.NotReversible;

    protected sealed override Task<UndoResult> Reverse(
        ShelfAwareDbContext db, TPayload payload, ActivityEntry entry, CancellationToken ct) =>
        Task.FromResult(UndoResult.NotReversible); // unreachable: the service refuses NotReversible before dispatch
}
