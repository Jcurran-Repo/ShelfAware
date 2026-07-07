using Microsoft.AspNetCore.Components;

namespace ShelfAware.Web.Components.Account;

/// <summary>Redirect helper for the static-SSR Account pages. With
/// <c>BlazorDisableThrowNavigationException</c> set (this project's default), <see cref="NavigationManager.NavigateTo(string)"/>
/// issues a real HTTP redirect and RETURNS — so callers must <c>return</c> after calling these.
/// Status messages ride a short-lived cookie across the redirect (static pages have no state).</summary>
public sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public const string StatusCookieName = "Identity.StatusMessage";

    private static readonly CookieBuilder StatusCookieBuilder = new()
    {
        SameSite = SameSiteMode.Strict,
        HttpOnly = true,
        IsEssential = true,
        MaxAge = TimeSpan.FromSeconds(5),
    };

    public void RedirectTo(string? uri)
    {
        uri ??= "";
        // Only same-site relative targets — a ReturnUrl query value must not become an open redirect.
        if (!Uri.IsWellFormedUriString(uri, UriKind.Relative))
        {
            uri = navigationManager.ToBaseRelativePath(uri);
        }
        navigationManager.NavigateTo(uri);
    }

    public void RedirectToWithStatus(string uri, string message, HttpContext context)
    {
        context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
        RedirectTo(uri);
    }

    public void RedirectToCurrentPage(HttpContext context) => RedirectTo(context.Request.Path);

    public void RedirectToCurrentPageWithStatus(string message, HttpContext context)
        => RedirectToWithStatus(context.Request.Path, message, context);
}
