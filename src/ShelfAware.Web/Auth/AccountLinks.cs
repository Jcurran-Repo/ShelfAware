using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ShelfAware.Web.Auth;

/// <summary>The ONE place that builds the email-confirmation link. Two pages send it (registration and
/// "resend the confirmation email") and one reads it back (ConfirmEmail), so a hand-built URL in each would
/// be three chances for the path or a parameter name to drift — and a drift there is a dead confirmation
/// link nobody can tell is dead. Centralising the shape means the senders and the reader agree by
/// construction. The token is Base64Url-encoded into the query string exactly as the reset link is;
/// ConfirmEmail decodes it the same way.</summary>
public static class AccountLinks
{
    /// <summary>An absolute confirm-email URL built from the live request (via <paramref name="nav"/>), so it
    /// carries whichever front door the registrant is at (demo.shelfaware.net, localhost, …), same as the
    /// reset link.</summary>
    public static string ConfirmEmailUrl(NavigationManager nav, string userId, string token)
    {
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        return nav.GetUriWithQueryParameters(
            nav.ToAbsoluteUri("Account/ConfirmEmail").AbsoluteUri,
            new Dictionary<string, object?> { ["userId"] = userId, ["code"] = code });
    }
}
