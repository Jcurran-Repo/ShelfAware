using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ShelfAware.Web.Auth;

/// <summary>The ONE place that builds the set-your-password link (the ResetPassword page URL carrying a
/// Base64Url-encoded token). Three senders reach it — a password reset, a resent reset, and a new-account
/// ACTIVATION (which is the same page: setting the password there also confirms the address) — so a
/// hand-built URL in each would be three chances for the path or a parameter name to drift, and a drift
/// there is a dead link nobody can tell is dead. Centralising the shape means every sender and the
/// ResetPassword reader agree by construction.</summary>
public static class AccountLinks
{
    /// <summary>An absolute ResetPassword URL built from the live request (via <paramref name="nav"/>), so it
    /// carries whichever front door the user is at (demo.shelfaware.net, localhost, …). The token is
    /// Base64Url-encoded into the <c>code</c> query parameter exactly as ResetPassword decodes it. No userId
    /// rides in the URL — ResetPassword matches the token against the email the user types, which is what
    /// keeps the address out of the link.</summary>
    public static string SetPasswordUrl(NavigationManager nav, string token)
    {
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        return nav.GetUriWithQueryParameters(
            nav.ToAbsoluteUri("Account/ResetPassword").AbsoluteUri,
            new Dictionary<string, object?> { ["code"] = code });
    }
}
