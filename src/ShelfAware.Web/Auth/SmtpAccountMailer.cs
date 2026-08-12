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
    public async Task SendPasswordResetAsync(string toEmail, string resetUrl, CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.IsConfigured)
        {
            throw new InvalidOperationException(
                "Email is not configured on this deployment; callers must gate on EmailOptions.IsConfigured.");
        }

        var message = BuildReset(o, toEmail, resetUrl);
        using var client = new SmtpClient();
        // The IsConfigured guard above guarantees SmtpHost (and From, used in BuildReset), and
        // startup validation pairs SmtpUser with SmtpPassword — the compiler just can't see
        // through the properties, hence the !s.
        // Auto: implicit TLS on 465, STARTTLS on 587 — whichever the configured port implies.
        await client.ConnectAsync(o.SmtpHost!, o.SmtpPort, SecureSocketOptions.Auto, ct);
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
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.From!));
        // Parse rather than new MailboxAddress(name, addr) so quoting/encoding stays MimeKit's
        // problem — but it is NOT a validation layer: MimeKit is lenient (a bare local part
        // parses). The recipient's shape is gated upstream by the form's [EmailAddress]; genuine
        // garbage past that fails at the relay, which the calling page logs.
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "Reset your Shelf Aware password";

        var body = new BodyBuilder
        {
            TextBody = $"""
                Someone asked to reset the Shelf Aware password for this address.

                Reset it here (the link expires after a day and stops working once used):
                {resetUrl}

                If this wasn't you, ignore this email — nothing has changed.
                """,
            HtmlBody = $"""
                <p>Someone asked to reset the Shelf Aware password for this address.</p>
                <p><a href="{System.Net.WebUtility.HtmlEncode(resetUrl)}">Reset your password</a>
                — the link expires after a day and stops working once used.</p>
                <p>If this wasn't you, ignore this email — nothing has changed.</p>
                """,
        };
        message.Body = body.ToMessageBody();
        return message;
    }
}
