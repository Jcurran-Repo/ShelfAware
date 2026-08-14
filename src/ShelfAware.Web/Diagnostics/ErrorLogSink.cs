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
