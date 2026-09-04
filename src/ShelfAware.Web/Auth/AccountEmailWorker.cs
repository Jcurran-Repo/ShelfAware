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
        // ONE reader, start to finish (so there is never a second concurrent drain racing this one). While
        // running, each send uses the stopping token, so a hung relay is abandoned promptly on shutdown
        // rather than wedging the pump. On stop, WaitToReadAsync eventually throws — but a channel hands back
        // a non-empty backlog through WaitToReadAsync/TryRead REGARDLESS of the token, so a job can be pulled
        // during shutdown; DrainOne sends any such job (and the final post-loop backlog) under a FRESH token,
        // so a restart delivers queued confirmation/reset mail instead of abandoning it. That token switch —
        // not the loop shape — is what makes the drain reliable; an earlier version that leaned on the loop
        // stopping "in time" abandoned buffered mail in a race the re-gate caught.
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (queue.Reader.TryRead(out var job))
                {
                    await DrainOne(job, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop requested; WaitToReadAsync may have thrown with jobs still buffered — the final drain sends them.
        }

        // Whatever is still buffered when the loop ends: send it, on this same reader thread, under a fresh
        // token. Bounded per send by SendTimeout; the host's own shutdown deadline bounds how long it waits
        // for this to finish before proceeding (anything unsent past that is lost only on a forced kill).
        var drained = 0;
        while (queue.Reader.TryRead(out var job))
        {
            await SendOneAsync(job, CancellationToken.None);
            drained++;
        }
        if (drained > 0)
        {
            logger.LogInformation("Drained {Count} queued account email(s) at shutdown.", drained);
        }
    }

    /// <summary>Send one job pulled by the running loop. Normally uses the stopping token (a hung send is
    /// abandoned fast on shutdown); once stop is requested, the channel can still hand back buffered jobs, so
    /// those go out under a fresh token — a restart should deliver them, not drop them.</summary>
    private Task DrainOne(AccountEmailJob job, CancellationToken stoppingToken) =>
        SendOneAsync(job, stoppingToken.IsCancellationRequested ? CancellationToken.None : stoppingToken);

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
