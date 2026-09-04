namespace ShelfAware.Web.Auth;

/// <summary>Drains <see cref="AccountEmailQueue"/> and sends each mail through the (singleton)
/// <see cref="IAccountMailer"/>, off the request thread. Same background-service shape as the error-log
/// writer: one reader, a per-job try/catch so one bad send can't kill the pump or stall the rest, and a
/// per-send timeout so a wedged relay can't block the whole queue behind it.</summary>
public sealed class AccountEmailWorker(
    AccountEmailQueue queue, IAccountMailer mailer, ILogger<AccountEmailWorker> logger) : BackgroundService
{
    // A hung relay must not wedge the (single-reader) worker forever, which would stall every mail behind
    // it. 30s is generous for a healthy SMTP round-trip; past it the send is abandoned and logged.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ReadAllAsync completes (throws OperationCanceledException) when the host stops; the outer try
        // lets that end the loop cleanly rather than surfacing as a crashed hosted service.
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(SendTimeout);
                try
                {
                    await SendAsync(job, timeout.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutting down mid-send — stop draining.
                    break;
                }
                catch (Exception ex)
                {
                    // A send failure (relay down, bad credentials, our own 30s timeout) is best-effort: the
                    // user saw the same generic "check your email" and can request another, so log and move
                    // to the next job. One bad send must not kill the worker or stall the queue.
                    logger.LogError(ex, "Sending a queued account email ({Kind}) failed.", job.Kind);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private Task SendAsync(AccountEmailJob job, CancellationToken ct) => job.Kind switch
    {
        AccountEmailKind.PasswordReset => mailer.SendPasswordResetAsync(job.ToEmail, job.Url, ct),
        AccountEmailKind.EmailConfirmation => mailer.SendEmailConfirmationAsync(job.ToEmail, job.Url, ct),
        AccountEmailKind.AlreadyRegistered => mailer.SendAlreadyRegisteredAsync(job.ToEmail, job.Url, ct),
        _ => Task.CompletedTask,
    };
}
