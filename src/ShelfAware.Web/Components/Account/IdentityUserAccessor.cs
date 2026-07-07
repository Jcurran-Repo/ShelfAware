using Microsoft.AspNetCore.Identity;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Components.Account;

/// <summary>Loads the signed-in <see cref="AppUser"/> for a static-SSR Account page from the request's
/// principal. A missing user (deleted account with a live cookie) bounces to login rather than crashing.</summary>
public sealed class IdentityUserAccessor(UserManager<AppUser> userManager, IdentityRedirectManager redirectManager)
{
    public async Task<AppUser?> GetUserAsync(HttpContext context)
    {
        var user = await userManager.GetUserAsync(context.User);
        if (user is null)
        {
            redirectManager.RedirectToWithStatus(
                "Account/Login", "Error: your account could not be loaded. Please sign in again.", context);
        }
        return user;
    }
}
