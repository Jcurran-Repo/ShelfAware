using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace ShelfAware.Web.Auth;

/// <summary>The kind of account email to send — <see cref="AccountEmailWorker"/> dispatches on it.</summary>
public enum AccountEmailKind
{
    PasswordReset,

    /// <summary>The set-your-password link a new account opens to activate itself on a confirmation-required
    /// box (<c>Auth:RequireEmailConfirmation</c>). Registration there creates the account with NO password;
    /// this link is where the person who controls the inbox sets it — which both establishes the credential
    /// and confirms the address. It links to the same ResetPassword page a reset does (via
    /// <see cref="AccountLinks.SetPasswordUrl"/>), which sets the password AND flips EmailConfirmed.</summary>
    Activation,

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
/// which is the only way "we dropped a confirmation email" reaches the log.
///
/// It is also the single choke point that BOUNDS outbound account mail — a per-recipient cooldown and a
/// box-wide daily cap (<see cref="EmailOptions.PerRecipientCooldownSeconds"/> /
/// <see cref="EmailOptions.DailyOutboundLimit"/>). This is a public, anonymous surface: without a bound, one
/// IP re-POSTing registration/forgot-password for an address could send at the /Account rate limit until the
/// provider's send quota is exhausted, silently breaking activation for everyone. A throttled/over-limit
/// mail is a logged drop on the request path — the page's response is UNCHANGED — so the bound adds no
/// account-enumeration oracle.</summary>
public sealed class AccountEmailQueue(ILogger<AccountEmailQueue> logger, IOptions<EmailOptions> email)
    : IAccountEmailQueue
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

    // Last time a mail was queued to each address (normalized case-insensitively), for the per-recipient
    // cooldown. Swept when it grows past the threshold — an entry older than the cooldown can never throttle.
    private const int CooldownMapSweepThreshold = 10_000;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSentTo = new(StringComparer.OrdinalIgnoreCase);

    // The global daily counter. A lock (not Interlocked) because the day-rollover reset and the increment
    // must be one atomic decision; contention is nil on a low-traffic account flow.
    private readonly object _dailyLock = new();
    private DateOnly _dailyDay;
    private int _dailyCount;

    public void Enqueue(AccountEmailJob job)
    {
        var o = email.Value;
        var recipient = job.ToEmail.Trim();
        var now = DateTimeOffset.UtcNow;

        // Per-recipient cooldown: refuse a second mail to the SAME address within the window. Stops the abuse
        // where someone re-POSTs registration or forgot-password for one address to burn the send quota, and
        // drops redundant rapid resends. A THROTTLED drop is silent on the request path (the page's redirect
        // is unchanged), so it adds no account-enumeration oracle — the whole reason the bound lives here and
        // not in a per-request response.
        if (o.PerRecipientCooldownSeconds > 0
            && _lastSentTo.TryGetValue(recipient, out var last)
            && now - last < TimeSpan.FromSeconds(o.PerRecipientCooldownSeconds))
        {
            logger.LogWarning("Account email throttled ({Kind}) — one was sent to this address moments ago.", job.Kind);
            return;
        }

        // Global daily cap (sized below the sending account's own quota): protects the provider account from
        // exhaustion/flagging and bounds total amplification, box-wide. Also a silent, logged drop.
        if (o.DailyOutboundLimit is int dailyLimit && !TryTakeDailySlot(dailyLimit))
        {
            logger.LogWarning("Account email dropped ({Kind}) — today's outbound send limit was reached.", job.Kind);
            return;
        }

        // TryWrite never blocks; false = the queue is full (Wait mode) OR CompleteWriter closed intake on
        // shutdown. Either way a dropped mail is best-effort — the user can ask for another — and dropping
        // beats blocking the request (which would reintroduce the timing/DoS this queue removes) or throwing.
        // Log it so a full queue or a shutdown-window drop is VISIBLE, not silently written to a dead channel.
        if (!_channel.Writer.TryWrite(job))
        {
            logger.LogWarning("Account email dropped ({Kind}) — the send queue is full or shutting down.", job.Kind);
            return;
        }

        _lastSentTo[recipient] = now;
        SweepCooldownMapIfLarge(now, o.PerRecipientCooldownSeconds);
    }

    /// <summary>Take a slot in today's global send budget, rolling the counter over at the local day boundary
    /// (the app's universal "today", as the account cap uses). Returns false when the day's limit is spent.
    /// A rare double-take under concurrency just caps a hair early — safe for a soft brake.</summary>
    private bool TryTakeDailySlot(int limit)
    {
        lock (_dailyLock)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (today != _dailyDay)
            {
                _dailyDay = today;
                _dailyCount = 0;
            }
            if (_dailyCount >= limit) return false;
            _dailyCount++;
            return true;
        }
    }

    /// <summary>Bound the cooldown map's memory: an entry older than the cooldown can never throttle, so
    /// sweep them out — but only once the map has grown past a threshold, so the common path stays O(1).</summary>
    private void SweepCooldownMapIfLarge(DateTimeOffset now, int cooldownSeconds)
    {
        if (_lastSentTo.Count <= CooldownMapSweepThreshold) return;
        var cutoff = now - TimeSpan.FromSeconds(Math.Max(cooldownSeconds, 1));
        foreach (var (key, when) in _lastSentTo)
        {
            if (when < cutoff) _lastSentTo.TryRemove(key, out _);
        }
    }

    /// <summary>Close intake on shutdown so the worker's drain finishes the buffered backlog and exits, and a
    /// mail enqueued AFTER the drain has emptied the channel is observably REFUSED by <see cref="Enqueue"/>
    /// (a logged drop) rather than silently written to a channel with no reader left. Same residual its
    /// sibling ErrorLogWriter closes the same way. Called once, from <see cref="AccountEmailWorker.StopAsync"/>.</summary>
    public void CompleteWriter() => _channel.Writer.TryComplete();

    /// <summary>The reader the worker drains. Internal — nothing but the worker should read the queue.</summary>
    internal ChannelReader<AccountEmailJob> Reader => _channel.Reader;
}
