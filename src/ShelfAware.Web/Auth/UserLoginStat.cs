namespace ShelfAware.Web.Auth;

/// <summary>One row per account: how many times it has signed in, and when first/last. The persisted
/// half of the admin's "who has logged in" view (the live half is <see cref="OnlinePresence"/>). Lives
/// in auth.db as OPERATOR data — no household owns "who logged in", it never reaches a data export or
/// "delete all my data", and the admin reader needs no tenancy bypass to read it (same posture as the
/// error log; see <see cref="AuthDbContext"/>).
///
/// Aggregate, not an event log: one row per user keyed on <see cref="UserId"/>, upserted on each login,
/// so it is naturally bounded (a handful of family accounts) with no trimming — unlike the error log's
/// per-occurrence rows. The lifetime total is the sum of <see cref="LoginCount"/>.</summary>
public class UserLoginStat
{
    /// <summary>The Identity user id — the key. Stable across an email change, so the count follows the
    /// account, not the address.</summary>
    public required string UserId { get; set; }

    /// <summary>The account's email at its most recent login (denormalized for display, refreshed each
    /// time so a changed address shows the current one). In this app the username IS the email.</summary>
    public required string Email { get; set; }

    public int LoginCount { get; set; }
    public DateTimeOffset FirstLoginAt { get; set; }
    public DateTimeOffset LastLoginAt { get; set; }
}
