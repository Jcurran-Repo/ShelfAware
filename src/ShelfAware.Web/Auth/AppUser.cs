using Microsoft.AspNetCore.Identity;

namespace ShelfAware.Web.Auth;

/// <summary>An account. Every user belongs to exactly one household (assigned at registration —
/// created fresh, or joined via an invite code); all pantry data is keyed by that household.</summary>
public class AppUser : IdentityUser
{
    public string? HouseholdId { get; set; }
}
