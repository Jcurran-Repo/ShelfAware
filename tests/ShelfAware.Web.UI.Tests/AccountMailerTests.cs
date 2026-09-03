using Microsoft.Extensions.Options;
using MimeKit;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The reset email itself, pinned without an SMTP server (BuildReset is the pure half of the
/// mailer; the network half is live-verified). Lives in this suite because BuildReset is internal
/// to ShelfAware.Web and this is the project with InternalsVisibleTo.
/// </summary>
public class AccountMailerTests
{
    private static EmailOptions Configured() => new()
    {
        SmtpHost = "smtp.example.test",
        From = "noreply@example.test",
        FromName = "Reginald",
    };

    [Fact]
    public void The_reset_message_carries_addressing_subject_and_the_link_in_both_bodies()
    {
        const string url = "https://family.example.test/Account/ResetPassword?code=abc123";

        var msg = SmtpAccountMailer.BuildReset(Configured(), "wife@example.test", url);

        var from = Assert.IsType<MailboxAddress>(Assert.Single(msg.From));
        Assert.Equal("Reginald", from.Name);
        Assert.Equal("noreply@example.test", from.Address);
        var to = Assert.IsType<MailboxAddress>(Assert.Single(msg.To));
        Assert.Equal("wife@example.test", to.Address);
        Assert.Equal("Reset your Reginald password", msg.Subject);
        // Both bodies, because mail clients pick one: a link missing from either is a reset
        // email that works in some inboxes.
        Assert.Contains(url, msg.TextBody);
        Assert.Contains(url, msg.HtmlBody);
    }

    [Fact]
    public void The_html_body_encodes_the_link()
    {
        // A '&' in the query is the ordinary case the encoding exists for — an unencoded one is
        // invalid HTML that happens to work until it doesn't.
        const string url = "https://x.test/Account/ResetPassword?code=abc&extra=1";

        var msg = SmtpAccountMailer.BuildReset(Configured(), "wife@example.test", url);

        Assert.Contains("code=abc&amp;extra=1", msg.HtmlBody);
        Assert.Contains("code=abc&extra=1", msg.TextBody); // and the text body stays raw
    }

    [Fact]
    public async Task An_unconfigured_mailer_refuses_loudly_instead_of_silently_sending_nothing()
    {
        // Every caller gates on EmailOptions.IsConfigured; reaching the mailer without it means a
        // gate is missing, and the caller is about to tell a user their email is on its way.
        var mailer = new SmtpAccountMailer(Options.Create(new EmailOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mailer.SendPasswordResetAsync("a@b.test", "https://x.test/reset"));
    }

    [Fact]
    public async Task An_unconfigured_mailer_refuses_the_confirmation_send_too()
    {
        // Same guard on the second mail — both go through the one SendAsync, which throws before touching
        // a network when Email: isn't configured.
        var mailer = new SmtpAccountMailer(Options.Create(new EmailOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mailer.SendEmailConfirmationAsync("a@b.test", "https://x.test/confirm"));
    }

    [Fact]
    public void The_confirmation_message_carries_addressing_subject_and_the_link_in_both_bodies()
    {
        // No '&' here so the raw link appears verbatim in both bodies; the '&'-encoding case (a real confirm
        // link has userId AND code) is the separate test below.
        const string url = "https://demo.example.test/Account/ConfirmEmail?code=abc123";

        var msg = SmtpAccountMailer.BuildConfirmation(Configured(), "visitor@example.test", url);

        var from = Assert.IsType<MailboxAddress>(Assert.Single(msg.From));
        Assert.Equal("Reginald", from.Name);
        Assert.Equal("noreply@example.test", from.Address);
        var to = Assert.IsType<MailboxAddress>(Assert.Single(msg.To));
        Assert.Equal("visitor@example.test", to.Address);
        Assert.Equal("Confirm your email for Reginald", msg.Subject);
        // Both bodies, because mail clients pick one — a link missing from either is a confirmation
        // email that works in some inboxes and not others.
        Assert.Contains(url, msg.TextBody);
        Assert.Contains(url, msg.HtmlBody);
    }

    [Fact]
    public void The_confirmation_html_body_encodes_the_link()
    {
        // The confirm link always has an '&' (userId AND code), so encoding it in the HTML body is the
        // ordinary case, not an edge — an unencoded ampersand is invalid HTML that works until it doesn't.
        const string url = "https://x.test/Account/ConfirmEmail?userId=u1&code=abc";

        var msg = SmtpAccountMailer.BuildConfirmation(Configured(), "visitor@example.test", url);

        Assert.Contains("userId=u1&amp;code=abc", msg.HtmlBody);
        Assert.Contains("userId=u1&code=abc", msg.TextBody); // and the text body stays raw
    }
}
