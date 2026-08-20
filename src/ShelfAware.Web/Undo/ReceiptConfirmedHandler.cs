using System.Text.Json;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a confirmed receipt: which receipt, the merchant + line count for
/// the summary, and its stored <see cref="Receipt.ImagePath"/> — captured at confirm time so the
/// post-commit hook can delete the image folder without a DB read (the receipt row is gone by then).</summary>
public sealed record ReceiptConfirmedPayload(int ReceiptId, string? Merchant, int LineCount, string ImagePath);

/// <summary>Undo for a confirmed receipt: remove the receipt AND everything its confirm did, through the
/// SAME <see cref="ReceiptRemovalService.RemoveOnAsync"/> the Upload page's ↩ Undo and the Receipts page's
/// "Remove receipt" run (one definition of "unwind a receipt" — purchases by provenance, products it
/// introduced only while they gained no other history, aliases it taught). The image folder is deleted in
/// <see cref="AfterCommitAsync"/>, not <see cref="Reverse"/>, because <see cref="Reverse"/> is what a Peek
/// runs to grey the row — a Peek must never touch the filesystem.
/// <para><see cref="UndoResult.Gone"/> when the receipt is already gone (the Upload/Receipts removal ran, or
/// another device undid it); <see cref="UndoResult.Superseded"/> for a receipt that can't be safely
/// reversed — a pre-provenance confirm — which a receipt WE recorded can't be, but the guard is honest.</para></summary>
public sealed class ReceiptConfirmedHandler(IReceiptImageCleanup imageCleanup)
    : UndoHandler<ReceiptConfirmedPayload>, IUndoAfterCommit
{
    public override ActivityKind Kind => ActivityKind.ReceiptConfirmed;

    protected override string Summarize(ReceiptConfirmedPayload p) =>
        p.Merchant is { Length: > 0 } merchant
            ? $"Confirmed {p.LineCount} item{(p.LineCount == 1 ? "" : "s")} from {merchant}"
            : $"Confirmed {p.LineCount} item{(p.LineCount == 1 ? "" : "s")}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, ReceiptConfirmedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var (outcome, _) = await ReceiptRemovalService.RemoveOnAsync(db, p.ReceiptId, ct);
        if (!outcome.Found) return UndoResult.Gone;          // already removed elsewhere
        if (outcome.Untraceable) return UndoResult.Superseded; // can't safely unwind (pre-provenance)
        return UndoResult.Done;
        // ⚠️ NOT the image folder here: this method is also what PeekAsync runs (then discards). The
        // filesystem delete rides AfterCommitAsync, which only a real undo reaches.
    }

    /// <summary>After the removal commits, forget the receipt's saved image — the receipt row is gone, so
    /// the folder comes from the payload, not the DB. Best-effort: <see cref="IReceiptImageCleanup"/> swallows
    /// its own IO errors, and an orphaned folder is harmless (and reached by "delete my data" anyway).</summary>
    public Task AfterCommitAsync(ActivityEntry entry, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ReceiptConfirmedPayload>(entry.PayloadJson);
        if (payload is not null) imageCleanup.DeleteFolder(payload.ImagePath);
        return Task.CompletedTask;
    }
}
