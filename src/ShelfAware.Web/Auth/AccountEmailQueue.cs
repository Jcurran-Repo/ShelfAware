using System.Threading.Channels;

namespace ShelfAware.Web.Auth;

/// <summary>The kind of account email to send — <see cref="AccountEmailWorker"/> dispatches on it.</summary>
public enum AccountEmailKind
{
    PasswordReset,
    EmailConfirmation,

    /// <summary>Sent to an address that tried to register but ALREADY has an account — so a duplicate
    /// registration returns the same "check your email" response as a real one and can't be used to
    /// enumerate existing accounts. Only ever queued where email is configured (the confirmation posture).</summary>
    AlreadyRegistered,
}

/// <summary>One queued outbound account email. Carries only captured strings — the recipient and the
/// absolute action URL the page built from the live request — so it has NO scoped dependency and the
/// worker (a singleton) can send it after the request that queued it has already ended.</summary>
public sealed record AccountEmailJob(AccountEmailKind Kind, string ToEmail, string Url);

/// <summary>Hands an account email to a background sender so the request thread never blocks on SMTP.
///
/// This is what makes outbound-mail timing UNIFORM: a real account and an unknown address both return in
/// ~milliseconds because neither waits for the ~1s SMTP round-trip, closing the account-existence timing
/// oracle that a synchronous send opens (a real account = slow, a miss = fast). It also stops a slow or
/// wedged relay from stalling registration or a password reset. Best-effort, like the synchronous send it
/// replaces: a full queue drops with a log line rather than blocking or throwing.</summary>
public interface IAccountEmailQueue
{
    /// <summary>Queue a mail for background delivery. Never blocks, never throws — safe to call mid-request.</summary>
    void Enqueue(AccountEmailJob job);
}

/// <summary>Bounded in-memory queue behind <see cref="IAccountEmailQueue"/>. Same shape as the error-log
/// sink (a bounded <see cref="Channel{T}"/> read by a single <see cref="AccountEmailWorker"/>): Wait mode +
/// TryWrite so a drop under flood is OBSERVABLE (TryWrite returns false rather than silently discarding),
/// which is the only way "we dropped a confirmation email" reaches the log.</summary>
public sealed class AccountEmailQueue(ILogger<AccountEmailQueue> logger) : IAccountEmailQueue
{
    // 1000 is far above any real burst for a low-traffic account flow; it exists so a runaway (or a wedged
    // worker) can't grow memory without bound. Wait mode means TryWrite reports a full queue instead of
    // dropping silently. SingleReader: exactly one worker drains it.
    private readonly Channel<AccountEmailJob> _channel = Channel.CreateBounded<AccountEmailJob>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    public void Enqueue(AccountEmailJob job)
    {
        // TryWrite never blocks; in Wait mode it returns false when the queue is full. A dropped
        // confirmation/reset is best-effort — the user can ask for another — and dropping is far better than
        // blocking the request (which would reintroduce the timing/DoS this queue exists to remove) or
        // throwing (the caller is mid-request). Log it so a chronically-full queue is visible.
        if (!_channel.Writer.TryWrite(job))
        {
            logger.LogWarning("Account email dropped ({Kind}) — the send queue is full.", job.Kind);
        }
    }

    /// <summary>The reader the worker drains. Internal — nothing but the worker should read the queue.</summary>
    internal ChannelReader<AccountEmailJob> Reader => _channel.Reader;
}
