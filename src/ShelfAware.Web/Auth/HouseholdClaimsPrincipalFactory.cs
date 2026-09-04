using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ShelfAware.Web.Auth;

/// <summary>Bakes the user's household id into the auth cookie as a claim, so every request (static
/// SSR, minimal APIs, and interactive circuits) can resolve the tenant without a DB round-trip. The claim
/// is minted here from the row at each sign-in and re-mint (RefreshSignInAsync), so it tracks the current
/// household — including the case where the household is chosen AFTER the first sign-in (a
/// confirmation-required box defers it to the chooser; the chooser refreshes the cookie so the new claim
/// appears). A member has exactly one household and it doesn't change under them; switching is future work
/// and would require a security-stamp bump to evict stale cookies/circuits.</summary>
public sealed class HouseholdClaimsPrincipalFactory(UserManager<AppUser> userManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser>(userManager, options)
{
    public const string HouseholdClaim = "shelfaware:household";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrEmpty(user.HouseholdId))
        {
            identity.AddClaim(new Claim(HouseholdClaim, user.HouseholdId));
        }
        return identity;
    }
}
