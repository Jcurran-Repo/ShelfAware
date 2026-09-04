namespace ShelfAware.Web.Auth;

/// <summary>Drains <see cref="AccountEmailQueue"/> and sends each mail through the (singleton)
/// <see cref="IAccountMailer"/>, off the request thread. Same background-service shape as the error-log
/// writer: one reader, a per-job try/catch so one bad send can't kill the pump or stall the rest, a per-send
/// timeout so a wedged relay can't block the queue behind it, and a shutdown DRAIN so a restart doesn't
/// silently discard queued confirmation/reset mail.</summary>
public sealed class AccountEmailWorker(
    AccountEmailQueue queue, IAccountMailer mailer, ILogger<AccountEmailWorker> logger) : BackgroundService
{
    // A hung relay must not wedge the (single-reader) worker forever, which would stall every mail behind
    // it. 30s is generous for a healthy SMTP round-trip; past it the send is abandoned and logged.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ReadAllAsync completes (throws OperationCanceledException) when the host stops; the try lets that
        // end the loop cleanly. The channel is never Complete()d, so shutdown is the only way out — and
        // whatever is still buffered is handled by StopAsync's drain below.
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await SendOneAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>On shutdown, send whatever is still buffered so a restart (e.g. a publish) doesn't silently
    /// drop a confirmation or reset link. Bounded by the host's shutdown timeout (<paramref
    /// name="cancellationToken"/>); anything still unsent when that elapses is LOGGED rather than lost
    /// invisibly (it can be re-requested).</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken); // signals ExecuteAsync's loop to stop and waits for it

        var undrained = 0;
        while (queue.Reader.TryRead(out var job))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                undrained++; // the shutdown deadline hit — count the rest rather than block
                continue;
            }
            await SendOneAsync(job, cancellationToken);
        }
        if (undrained > 0)
        {
            logger.LogWarning(
                "{Count} account email(s) were still queued at shutdown and weren't sent — they can be re-requested.",
                undrained);
        }
    }

    /// <summary>Send one job with its own timeout, swallowing every failure (logged) so the caller — the
    /// loop or the drain — never sees an exception and always moves to the next job.</summary>
    private async Task SendOneAsync(AccountEmailJob job, CancellationToken shutdownToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await Dispatch(job, timeout.Token);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // The host is stopping mid-send — abandon this one; StopAsync's undrained count covers the rest.
            logger.LogWarning("A queued account email ({Kind}) was abandoned at shutdown.", job.Kind);
        }
        catch (OperationCanceledException)
        {
            // Our own 30s timeout fired (the relay hung) — distinct from a shutdown, and worth naming.
            logger.LogError("Sending a queued account email ({Kind}) timed out.", job.Kind);
        }
        catch (Exception ex)
        {
            // A send failure (relay down, bad credentials) is best-effort: the user saw the same generic
            // "check your email" and can request another. Log and move on; one bad send must not kill the pump.
            logger.LogError(ex, "Sending a queued account email ({Kind}) failed.", job.Kind);
        }
    }

    private Task Dispatch(AccountEmailJob job, CancellationToken ct) => job.Kind switch
    {
        AccountEmailKind.PasswordReset => mailer.SendPasswordResetAsync(job.ToEmail, job.Url, ct),
        AccountEmailKind.EmailConfirmation => mailer.SendEmailConfirmationAsync(job.ToEmail, job.Url, ct),
        AccountEmailKind.AlreadyRegistered => mailer.SendAlreadyRegisteredAsync(job.ToEmail, job.Url, ct),
        _ => Task.CompletedTask,
    };
}
