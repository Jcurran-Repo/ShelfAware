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

    public void RedirectTo(string? uri) => navigationManager.NavigateTo(SafeTarget(uri, navigationManager.BaseUri));

    /// <summary>
    /// The one reading of "may a <c>ReturnUrl</c> be followed?" — every Account page redirect goes through
    /// here, so a sign-in link can never become an open redirect. Anything that isn't a same-site target
    /// resolves to the app root rather than throwing or leaving the site.
    ///
    /// <para>⚠️ The template this replaced guarded with <c>Uri.IsWellFormedUriString(uri, UriKind.Relative)</c>
    /// alone. A protocol-relative <c>//evil.example</c> is a well-formed RELATIVE reference by that test, and
    /// the browser resolves it against the current scheme — i.e. off the site — so
    /// <c>/Account/Login?ReturnUrl=//evil.example</c> would land a freshly signed-in user on another host.
    /// <c>/\evil.example</c> is the same trick for browsers that normalise backslashes. Those two shapes are
    /// refused here by name, matching ASP.NET Core's own <c>IUrlHelper.IsLocalUrl</c> rule. An absolute URL
    /// is followed only when it is under this app's base (the template threw an
    /// <see cref="ArgumentException"/> on any other absolute URL — a 500 on a tampered link, which is the
    /// wrong answer to a bad query string).</para>
    /// </summary>
    /// <param name="uri">The requested target: a <c>ReturnUrl</c> query value, a request path, or null.</param>
    /// <param name="baseUri">The app's base URI (<see cref="NavigationManager.BaseUri"/>), always with a
    /// trailing slash.</param>
    /// <returns>A base-relative or rooted path safe to hand to <see cref="NavigationManager.NavigateTo(string)"/>;
    /// an empty string means the app root.</returns>
    internal static string SafeTarget(string? uri, string baseUri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return "";
        uri = uri.Trim();

        if (IsProtocolRelative(uri)) return "";
        // A browser deletes tabs and newlines from a URL before reading it, so "/\t/evil.example" is
        // "//evil.example" by the time it is followed. No real ReturnUrl carries a control character.
        if (uri.Any(char.IsControl)) return "";

        // Rooted path ("/products?tag=x"): the only kind a ReturnUrl is meant to carry. The two
        // protocol-relative shapes were refused above, so what's left stays on this host.
        // Checked BEFORE TryCreate(Absolute): on Unix "/products" parses as an absolute file:// URI.
        if (uri[0] == '/') return uri;

        if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute))
        {
            // An absolute URL — including "javascript:", "mailto:", and any other scheme — is followed only
            // when it is this site, and then as the base-relative path NavigateTo expects.
            // ⚠️ The remainder goes back through this same reading, because NavigateTo resolves it against
            // the base: "https://this.site///evil.example" leaves "//evil.example" (Uri turns backslashes into
            // slashes first, so those reach the same shape), and "https://this.site/https://evil.example"
            // leaves an absolute foreign URL. Each pass strips the base, so the recursion is bounded.
            var target = absolute.AbsoluteUri;
            return target.StartsWith(baseUri, StringComparison.OrdinalIgnoreCase)
                ? SafeTarget(target[baseUri.Length..], baseUri)
                : "";
        }

        // A plain relative path ("Account/Login"), as the Account pages themselves link.
        return uri;
    }

    /// <summary>"//host" or "/\host" (either slash direction in either position) — the shapes a browser
    /// resolves to another origin although no scheme is present.</summary>
    private static bool IsProtocolRelative(string uri) =>
        uri.Length >= 2
        && (uri[0] == '/' || uri[0] == '\\')
        && (uri[1] == '/' || uri[1] == '\\');

    public void RedirectToWithStatus(string uri, string message, HttpContext context)
    {
        context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
        RedirectTo(uri);
    }

    public void RedirectToCurrentPage(HttpContext context) => RedirectTo(context.Request.Path);

    public void RedirectToCurrentPageWithStatus(string message, HttpContext context)
        => RedirectToWithStatus(context.Request.Path, message, context);
}
