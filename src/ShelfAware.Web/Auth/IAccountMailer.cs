namespace ShelfAware.Web.Auth;

/// <summary>Sends the app's account email (today: the password reset). One SMTP implementation
/// behind it; the seam exists so tests can capture sends and a provider swap stays config plus one
/// class, never a hunt.</summary>
public interface IAccountMailer
{
    /// <summary>Send the password-reset email. <paramref name="resetUrl"/> is a fully absolute URL
    /// built by the page from the live request, so it carries whichever front door the user is
    /// actually at (family.shelfaware.net, the tailnet name, localhost). Throws
    /// <see cref="InvalidOperationException"/> on an unconfigured deployment: every caller gates on
    /// <see cref="EmailOptions.IsConfigured"/> first, so reaching that throw means a gate is missing
    /// — fail loud, not silent, because the caller is about to tell a user their email is on its way.</summary>
    Task SendPasswordResetAsync(string toEmail, string resetUrl, CancellationToken ct = default);
}
