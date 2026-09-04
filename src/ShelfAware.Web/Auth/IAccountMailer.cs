namespace ShelfAware.Web.Auth;

/// <summary>Sends the app's account email (the password reset, the new-account activation link, and the
/// already-registered notice). One SMTP
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

    /// <summary>Send the activation link a new account opens to SET ITS PASSWORD and thereby confirm its
    /// address (the demo box's <c>Auth:RequireEmailConfirmation</c>, where registration creates a passwordless
    /// account). <paramref name="setPasswordUrl"/> is a fully absolute URL built by the page from the live
    /// request, same as the reset link — it carries whichever front door the registrant is actually at.</summary>
    Task SendAccountActivationAsync(string toEmail, string setPasswordUrl, CancellationToken ct = default);

    /// <summary>Send the "you already have an account" notice to an address that tried to register but is
    /// already taken — so a duplicate registration returns the SAME response as a real one and can't be used
    /// to enumerate existing accounts, while the real owner still learns what happened.
    /// <paramref name="signInUrl"/> is the absolute sign-in URL built from the live request.</summary>
    Task SendAlreadyRegisteredAsync(string toEmail, string signInUrl, CancellationToken ct = default);
}
