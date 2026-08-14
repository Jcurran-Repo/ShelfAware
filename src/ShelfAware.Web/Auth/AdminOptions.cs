using System.Security.Claims;

namespace ShelfAware.Web.Auth;

/// <summary>The config-designated admin ("Admin" section). Same posture as Google OAuth and the
/// Email section: unset means the feature does not exist — no admin link renders, the admin page
/// refuses everyone, and the bug-report form is not offered (a report nobody will read must not be
/// invited). Deliberately no ValidateOnStart: an empty section is the normal self-host state, not
/// a misconfiguration.</summary>
public class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>The authorization policy name. Every admin gate — the routed page's policy, the
    /// footer's AuthorizeView, the reader's defense-in-depth check — resolves through
    /// <see cref="IsAdmin"/>, so "who is the admin" has exactly one definition.</summary>
    public const string PolicyName = "Admin";

    /// <summary>Sign-in emails granted the admin view. Matching is trimmed and case-insensitive.</summary>
    public string[] Emails { get; set; } = [];

    /// <summary>THE one definition of "this deployment has an admin" — the surfaces that gate on
    /// it (footer link, bug-report form) must not re-derive it.</summary>
    public bool Configured => Emails.Any(e => !string.IsNullOrWhiteSpace(e));

    /// <summary>Whether this signed-in principal is the admin. Reads Identity.Name because in this
    /// app the username IS the email: Register, ExternalLogin and DevAuth all create accounts with
    /// UserName = Email and nothing ever sets them apart (RequireUniqueEmail pins the pairing) —
    /// and Name is the claim Identity actually puts in the cookie; there is no separate email
    /// claim (see HouseholdClaimsPrincipalFactory).</summary>
    public bool IsAdmin(ClaimsPrincipal user)
    {
        if (user.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } email }) return false;
        return Emails.Any(e => string.Equals(e.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
