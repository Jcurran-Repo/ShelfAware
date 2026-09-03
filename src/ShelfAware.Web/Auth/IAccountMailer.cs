namespace ShelfAware.Web.Auth;

/// <summary>Sends the app's account email (the password reset, and the email-confirmation link). One SMTP
/// implementation behind it; the seam exists so tests can capture sends and a provider swap stays config
/// plus one class, never a hunt. Every method throws <see cref="InvalidOperationException"/> on an
/// unconfigured deployment: callers gate on <see cref="EmailOptions.IsConfigured"/> first, so reaching a
/// throw means a gate is missing — fail loud, not silent, because the caller is about to tell a user their
/// email is on its way.</summary>
public interface IAccountMailer
{
    /// <summary>Send the password-reset email. <paramref name="resetUrl"/> is a fully absolute URL
    /// built by the page from the live request, so it carries whichever front door the user is
    /// actually at (family.shelfaware.net, the tailnet name, localhost).</summary>
    Task SendPasswordResetAsync(string toEmail, string resetUrl, CancellationToken ct = default);

    /// <summary>Send the email-confirmation link a new account must open before it can sign in (the demo
    /// box's <c>Auth:RequireEmailConfirmation</c>). <paramref name="confirmUrl"/> is a fully absolute URL
    /// built by the page from the live request, same as the reset link — it carries whichever front door the
    /// registrant is actually at.</summary>
    Task SendEmailConfirmationAsync(string toEmail, string confirmUrl, CancellationToken ct = default);
}
