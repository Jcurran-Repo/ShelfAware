using System.Threading.Channels;

namespace ShelfAware.Web.Diagnostics;

/// <summary>A captured Error/Critical log event, queued for the background writer.</summary>
public sealed record CapturedError(
    DateTimeOffset At,
    string Level,
    string Category,
    string? ExceptionType,
    string? MessageTemplate,
    string Message,
    string? ExceptionDetail);

/// <summary>The bounded hand-off between the capture provider (called on every LogError, so it
/// must never block or throw) and the background writer. On overflow the newest event is dropped
/// and COUNTED — the admin page shows the count, so load-shedding is visible rather than silent
/// (the "no silent caps" rule). Wait-mode + TryWrite is what makes a drop observable: DropOldest/
/// DropWrite modes discard inside the channel where no caller can count them.</summary>
public sealed class ErrorLogSink
{
    public const int Capacity = 256;
    private long _dropped;

    public Channel<CapturedError> Channel { get; } =
        System.Threading.Channels.Channel.CreateBounded<CapturedError>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });

    /// <summary>Events lost since startup — to a full queue or a capture/persist failure.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    // The category exclusion alone is NOT a complete recursion break: a FAILING persist makes EF
    // itself log at Error under Microsoft.EntityFrameworkCore.Database.Command — a category the
    // capture must otherwise keep watching, because the app's own EF failures are exactly what an
    // error log is for — and capturing that echo turns a persistently failing auth.db into a
    // self-feeding busy loop (probed: one failed persist regenerated exactly one event). The
    // writer opens this scope around each persist; AsyncLocal flows into EF's logging on that
    // call's own async context and nowhere else. Suppressed events are skipped WITHOUT counting:
    // they are the pipeline's own exhaust — the writer already counts and logs the failure once.
    private static readonly AsyncLocal<bool> _persisting = new();

    public static bool CaptureSuppressed => _persisting.Value;

    public static PersistScope BeginPersist() => new();

    public readonly struct PersistScope : IDisposable
    {
        public PersistScope() => _persisting.Value = true;
        public void Dispose() => _persisting.Value = false;
    }

    /// <summary>Non-blocking post. A full channel refuses the write; the event becomes a counted
    /// drop instead of a blocked log call.</summary>
    public bool TryPost(CapturedError e)
    {
        if (Channel.Writer.TryWrite(e)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public void CountDrop() => Interlocked.Increment(ref _dropped);
}
