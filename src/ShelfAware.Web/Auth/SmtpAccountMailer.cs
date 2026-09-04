using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ShelfAware.Web.Auth;

/// <summary>MailKit-backed <see cref="IAccountMailer"/>. MailKit rather than System.Net.Mail's
/// SmtpClient because Microsoft's own docs steer new code away from the latter. A fresh client per
/// send — SmtpClient instances aren't safe for concurrent reuse, and this sends a handful of mails
/// a month, not a queue.</summary>
public sealed class SmtpAccountMailer(IOptions<EmailOptions> options) : IAccountMailer
{
    public Task SendPasswordResetAsync(string toEmail, string resetUrl, CancellationToken ct = default)
        => SendAsync(o => BuildReset(o, toEmail, resetUrl), ct);

    public Task SendAccountActivationAsync(string toEmail, string setPasswordUrl, CancellationToken ct = default)
        => SendAsync(o => BuildActivation(o, toEmail, setPasswordUrl), ct);

    public Task SendAlreadyRegisteredAsync(string toEmail, string signInUrl, CancellationToken ct = default)
        => SendAsync(o => BuildAlreadyRegistered(o, toEmail, signInUrl), ct);

    /// <summary>The one place that connects, authenticates, sends, and disconnects — so every mail shares
    /// exactly one TLS/auth policy and none can drift. <paramref name="build"/> runs only after the
    /// configured guard, so BuildReset/BuildActivation can assume a satisfied <see cref="EmailOptions"/>.</summary>
    private async Task SendAsync(Func<EmailOptions, MimeMessage> build, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured)
        {
            throw new InvalidOperationException(
                "Email is not configured on this deployment; callers must gate on EmailOptions.IsConfigured.");
        }

        var message = build(o);
        using var client = new SmtpClient();
        // The IsConfigured guard above guarantees SmtpHost (and From, used in the builders), and startup
        // validation pairs SmtpUser with SmtpPassword — the compiler just can't see through the properties,
        // hence the !s.
        // 465 = implicit TLS; anything else = STARTTLS, MANDATORY — not SecureSocketOptions.Auto, whose 587
        // behavior is StartTls*WhenAvailable*: against a server (or an active attacker stripping the EHLO)
        // that offers none, Auto continues in cleartext, credentials and the link included. StartTls fails
        // closed instead.
        var tls = o.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await client.ConnectAsync(o.SmtpHost!, o.SmtpPort, tls, ct);
        if (!string.IsNullOrWhiteSpace(o.SmtpUser))
        {
            await client.AuthenticateAsync(o.SmtpUser, o.SmtpPassword!, ct);
        }
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    /// <summary>Internal so tests can pin the message without an SMTP server: addressing, subject,
    /// and that BOTH bodies carry the link. <paramref name="o"/> must satisfy
    /// <see cref="EmailOptions.IsConfigured"/> — the caller's guard, not re-checked here.</summary>
    internal static MimeMessage BuildReset(EmailOptions o, string toEmail, string resetUrl)
        => Build(o, toEmail,
            subject: "Reset your Reginald password",
            text: $"""
                Someone asked to reset the Reginald password for this address.

                Reset it here (the link expires after a day and stops working once used):
                {resetUrl}

                If this wasn't you, ignore this email — nothing has changed.
                """,
            html: $"""
                <p>Someone asked to reset the Reginald password for this address.</p>
                <p><a href="{System.Net.WebUtility.HtmlEncode(resetUrl)}">Reset your password</a>
                — the link expires after a day and stops working once used.</p>
                <p>If this wasn't you, ignore this email — nothing has changed.</p>
                """);

    /// <summary>Internal, same reasoning as <see cref="BuildReset"/>: the activation mail's addressing,
    /// subject, and that BOTH bodies carry the link. <paramref name="o"/> must satisfy
    /// <see cref="EmailOptions.IsConfigured"/> — the caller's guard, not re-checked here. The link goes to the
    /// same ResetPassword page a reset uses: setting the password there activates the account (and confirms
    /// the address). Setting the password is what proves inbox control, so no one who merely knew the email
    /// at sign-up can take the account over.</summary>
    internal static MimeMessage BuildActivation(EmailOptions o, string toEmail, string setPasswordUrl)
        => Build(o, toEmail,
            subject: "Set your password to activate Reginald",
            text: $"""
                Welcome to Reginald! Set your password to activate your account and sign in
                (the link expires after a day and stops working once used):
                {setPasswordUrl}

                If you didn't sign up, ignore this email — the account stays locked until a password is set.
                """,
            html: $"""
                <p>Welcome to Reginald! Set your password to activate your account and sign in.</p>
                <p><a href="{System.Net.WebUtility.HtmlEncode(setPasswordUrl)}">Set my password</a>
                — the link expires after a day and stops working once used.</p>
                <p>If you didn't sign up, ignore this email — the account stays locked until a password is set.</p>
                """);

    /// <summary>Internal, same reasoning as the others: the already-registered notice's addressing, subject,
    /// and that BOTH bodies carry the sign-in link. <paramref name="o"/> must satisfy
    /// <see cref="EmailOptions.IsConfigured"/> — the caller's guard, not re-checked here.</summary>
    internal static MimeMessage BuildAlreadyRegistered(EmailOptions o, string toEmail, string signInUrl)
        => Build(o, toEmail,
            subject: "You already have a Reginald account",
            text: $"""
                Someone just tried to create a Reginald account with this email address — but you already
                have one.

                If that was you, just sign in: {signInUrl}
                Forgotten your password? Use the "Forgot your password?" link on that page.

                If it wasn't you, ignore this email — no new account was created and nothing changed.
                """,
            html: $"""
                <p>Someone just tried to create a Reginald account with this email address — but you already have one.</p>
                <p>If that was you, just <a href="{System.Net.WebUtility.HtmlEncode(signInUrl)}">sign in</a>.
                Forgotten your password? Use the "Forgot your password?" link on that page.</p>
                <p>If it wasn't you, ignore this email — no new account was created and nothing changed.</p>
                """);

    /// <summary>Shared envelope for every account mail: From from the configured identity, the recipient,
    /// the subject, and both a text and an HTML body (mail clients pick one, so a link missing from either
    /// is a mail that works in some inboxes and not others).</summary>
    private static MimeMessage Build(EmailOptions o, string toEmail, string subject, string text, string html)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.From!));
        // Parse rather than new MailboxAddress(name, addr) so quoting/encoding stays MimeKit's problem — but
        // it is NOT a validation layer: MimeKit is lenient (a bare local part parses). The recipient's shape
        // is gated upstream by the form's [EmailAddress]; genuine garbage past that fails at the relay, which
        // the calling page logs.
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();
        return message;
    }
}
