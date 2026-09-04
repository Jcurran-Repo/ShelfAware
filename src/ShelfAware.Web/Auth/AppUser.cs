using Microsoft.AspNetCore.Identity;

namespace ShelfAware.Web.Auth;

/// <summary>An account. Every user belongs to exactly one household (assigned at registration —
/// created fresh, or joined via an invite code); all pantry data is keyed by that household.</summary>
public class AppUser : IdentityUser
{
    public string? HouseholdId { get; set; }

    /// <summary>The day this account was created (server-local), or NULL for accounts made before the
    /// column existed. The demo box's daily account-creation cap counts rows with today's value, so the
    /// count IS the number of accounts made today — no separate counter to keep honest. A DateOnly (not
    /// DateTimeOffset) so it filters cleanly in a query, matching AiUsage.Day; NULL on pre-feature rows,
    /// which never match "== today" and so never inflate the cap.</summary>
    public DateOnly? CreatedOn { get; set; }

    /// <summary>The one way a production account is constructed, so <see cref="CreatedOn"/> — which the
    /// account-creation cap counts — is stamped in ONE place rather than repeated at every creation site
    /// (Register / ExternalLogin / DevAuth). A future creation path that forgets the stamp would silently
    /// escape the cap with a green suite; funnelling through here makes the right thing the easy thing.
    /// <paramref name="emailConfirmed"/> is true for provider-asserted (OAuth) or dev accounts, false for a
    /// self-registered one that must confirm. Tests set <see cref="CreatedOn"/> directly rather than call
    /// this, since they need a fixed day, not today's.</summary>
    public static AppUser NewToday(string email, bool emailConfirmed = false) => new()
    {
        UserName = email,
        Email = email,
        EmailConfirmed = emailConfirmed,
        CreatedOn = DateOnly.FromDateTime(DateTime.Today),
    };
}
