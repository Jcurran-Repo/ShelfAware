using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a recorded inventory signal: the exact signal row to delete, plus
/// its kind and product name for the summary.</summary>
public sealed record SignalRecordedPayload(int SignalId, SignalKind Kind, string ProductName);

/// <summary>Undo for a recorded signal (the dashboard/grocery/chat "Restocked / Out / Running low").
/// Deletes the exact InventorySignal row it created — which is the whole reversal, because a signal's
/// effect is derived from its row: deleting a Restocked un-clears the OutNow it cleared, and deleting an
/// OutNow removes the outage. If the row is already gone, undoing would do nothing, so it reports
/// <see cref="UndoResult.Gone"/> — the page shows the entry greyed rather than a button that no-ops.</summary>
public sealed class SignalRecordedHandler : UndoHandler<SignalRecordedPayload>
{
    public override ActivityKind Kind => ActivityKind.SignalRecorded;

    protected override string Summarize(SignalRecordedPayload p) => p.Kind switch
    {
        SignalKind.Restocked => $"Restocked {p.ProductName}",
        SignalKind.OutNow => $"Marked {p.ProductName} out",
        SignalKind.RunningLow => $"Marked {p.ProductName} running low",
        _ => $"Signalled {p.ProductName}",
    };

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, SignalRecordedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var signal = await db.InventorySignals.FirstOrDefaultAsync(s => s.Id == p.SignalId, ct);
        if (signal is null) return UndoResult.Gone; // already gone — undoing would be a no-op
        db.InventorySignals.Remove(signal);
        return UndoResult.Done;
    }
}
