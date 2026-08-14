namespace ShelfAware.Web.Diagnostics;

/// <summary>Drains the sink and persists each captured error — the async half that keeps DB IO
/// off the logging hot path. Logs its own troubles under this Diagnostics category, which the
/// capture provider EXCLUDES: that exclusion is the recursion break, and without it a failing
/// writer would feed itself forever.</summary>
public sealed class ErrorLogWriter(ErrorLogSink sink, ErrorLogStore store, ILogger<ErrorLogWriter> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var e in sink.Channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await store.RecordAsync(e, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw; // shutdown, not a failure
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
}
