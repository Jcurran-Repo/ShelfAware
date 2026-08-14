namespace ShelfAware.Web.Diagnostics;

/// <summary>Drains the sink and persists each captured error — the async half that keeps DB IO
/// off the logging hot path. Two recursion breaks keep a failing pipeline from feeding itself:
/// this class logs its own troubles under a Diagnostics category the capture provider excludes,
/// and each persist runs inside <see cref="ErrorLogSink.BeginPersist"/> so EF's own Error logging
/// about a FAILED persist is not captured either.</summary>
public sealed class ErrorLogWriter(ErrorLogSink sink, ErrorLogStore store, ILogger<ErrorLogWriter> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // CancellationToken.None on the read and the record, deliberately: shutdown DRAINS the
        // queue (StopAsync completes the channel; the loop finishes what is left and exits)
        // instead of abandoning up to Capacity events uncounted. A DB call hanging during the
        // drain is bounded by the host's shutdown timeout, which abandons the task — that trade
        // is the price of never losing queued events silently on an ordinary restart.
        await foreach (var e in sink.Channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                using (ErrorLogSink.BeginPersist())
                {
                    await store.RecordAsync(e, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                // An error about errors goes to the ordinary log (this category is excluded from
                // capture) and the event is counted as dropped, so the loss is visible in-app.
                sink.CountDrop();
                logger.LogWarning(ex, "Couldn't persist a captured error event.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        sink.Channel.Writer.TryComplete(); // no more intake; the drain loop finishes the queue
        await base.StopAsync(cancellationToken);
        // The runtime may start ExecuteAsync through a cancellable hop, so a stop racing a
        // just-started service can find the loop CANCELLED WITHOUT EVER RUNNING (observed under a
        // saturated thread pool). Whatever is still queued then is lost to the process exit —
        // sweep it into the drop count so even this loss isn't silent.
        //
        // Known residuals, both confined to a FORCED-timeout teardown (the host's shutdown token
        // firing while a store call hangs mid-drain — the process is being killed): this TryRead
        // then runs beside the still-live loop, leaning on the current BCL bounded channel being
        // safe for a second reader despite SingleReader = true; and the one event the loop holds
        // in flight at that moment dies with the process uncounted. Reviewer-verified unreachable
        // outside that teardown; documented rather than fixed, because a fix would orchestrate a
        // path whose trigger is the process already being killed.
        while (sink.Channel.Reader.TryRead(out _)) sink.CountDrop();
    }
}
