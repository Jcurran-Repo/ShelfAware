using System.Collections.Concurrent;

namespace ShelfAware.Web.Auth;

/// <summary>An online account and how it presents to the admin: who, how many live connections (tabs),
/// and since when the earliest of them connected.</summary>
public sealed record OnlineUser(string UserId, string Email);
public sealed record OnlineEntry(OnlineUser User, int Connections, DateTimeOffset Since);

/// <summary>Tracks who is connected RIGHT NOW — the live half of the admin's "who's logged in" view
/// (the persisted half is <see cref="LoginAudit"/>). A process-wide singleton keyed by circuit id, fed
/// by <see cref="PresenceCircuitHandler"/> as SignalR connections come up and go down. In-memory and
/// ephemeral by design: presence is a now-fact, not history, so it needs no persistence and a restart
/// correctly shows nobody until circuits reconnect.
///
/// "Online" means an active connection (a browser tab talking to the server), which is why it is driven
/// by connection up/down rather than circuit open/close — a closed tab drops promptly instead of
/// lingering through the reconnection-retention window. Snapshots DEDUPE by account: three tabs are one
/// person online, with a connection count. <see cref="Changed"/> lets the admin page re-render live.</summary>
public sealed class OnlinePresence
{
    private readonly ConcurrentDictionary<string, (OnlineUser User, DateTimeOffset At)> _circuits = new();

    /// <summary>Raised whenever the online set changes (a connection came up or went down), so a viewer
    /// can re-render. Handlers run on the CHANGING circuit's thread — a subscriber that touches its own
    /// component must marshal (InvokeAsync) and must not throw.</summary>
    public event Action? Changed;

    /// <summary>A connection for <paramref name="circuitId"/> came up. Idempotent per circuit; the
    /// timestamp is kept from the first connect so "since" reflects when they actually arrived.</summary>
    public void Connect(string circuitId, OnlineUser user, DateTimeOffset at)
    {
        var added = false;
        _circuits.AddOrUpdate(circuitId,
            _ => { added = true; return (user, at); },
            (_, existing) => existing); // a reconnect keeps the original arrival time
        if (added) Changed?.Invoke();
    }

    /// <summary>A connection for <paramref name="circuitId"/> went down (tab closed, network drop, or
    /// circuit disposed). Idempotent — only fires <see cref="Changed"/> if it actually removed one.</summary>
    public void Disconnect(string circuitId)
    {
        if (_circuits.TryRemove(circuitId, out _)) Changed?.Invoke();
    }

    /// <summary>The online accounts, one entry per account (tabs collapsed to a connection count),
    /// email-ordered. "Since" is the earliest of the account's connections.</summary>
    public IReadOnlyList<OnlineEntry> Snapshot() =>
        [.. _circuits.Values
            .GroupBy(c => c.User.UserId)
            .Select(g => new OnlineEntry(g.First().User, g.Count(), g.Min(c => c.At)))
            .OrderBy(e => e.User.Email, StringComparer.OrdinalIgnoreCase)];

    /// <summary>How many distinct accounts are online (not how many connections).</summary>
    public int OnlineCount => _circuits.Values.Select(c => c.User.UserId).Distinct().Count();
}
