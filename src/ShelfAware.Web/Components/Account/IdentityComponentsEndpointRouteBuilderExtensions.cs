using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Components.Account;

public static class IdentityComponentsEndpointRouteBuilderExtensions
{
    /// <summary>The endpoints Identity needs OUTSIDE the component router. Logout must be a real HTTP
    /// POST (an interactive circuit can't clear auth cookies), sent from the plain HTML form in the
    /// layout header. Reading the antiforgery-protected form binds validation to the signed-in user.</summary>
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var accountGroup = endpoints.MapGroup("/Account").RequireAuthorization();

        accountGroup.MapPost("/Logout", async Task<RedirectHttpResult> (
            HttpContext context,
            SignInManager<AppUser> signInManager,
            UserManager<AppUser> userManager,
            [FromForm] string? returnUrl) =>
        {
            // "Logout kills everything": bumping the security stamp fails stamp revalidation on every
            // live circuit and device within one revalidation interval; the sign-out then clears THIS
            // browser's cookie, and the full-page redirect tears down this tab's circuit immediately.
            var user = await userManager.GetUserAsync(context.User);
            if (user is not null)
            {
                await userManager.UpdateSecurityStampAsync(user);
            }
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect("~/Account/Login");
        });

        return accountGroup;
    }
}
