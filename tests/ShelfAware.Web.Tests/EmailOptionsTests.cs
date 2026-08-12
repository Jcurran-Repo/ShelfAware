using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The three properties Program.cs's startup validation and every feature gate are built from.
/// The rule they compose to: a deployment is either wholly without email (feature off everywhere)
/// or fully configured — anything in between refuses to boot rather than half-working.
/// </summary>
public class EmailOptionsTests
{
    [Fact]
    public void An_empty_section_is_wholly_absent_and_not_configured()
    {
        var o = new EmailOptions();
        Assert.False(o.IsConfigured);
        Assert.True(o.IsWhollyAbsent);
        Assert.True(o.CredentialsPaired);
    }

    [Fact]
    public void Whitespace_counts_as_absent_not_as_a_value()
    {
        var o = new EmailOptions { SmtpHost = "  ", From = "\t", SmtpUser = " ", SmtpPassword = "" };
        Assert.False(o.IsConfigured);
        Assert.True(o.IsWhollyAbsent);
        Assert.True(o.CredentialsPaired);
    }

    [Fact]
    public void Host_plus_from_is_configured_with_or_without_credentials()
    {
        var open = new EmailOptions { SmtpHost = "smtp.example.test", From = "noreply@example.test" };
        Assert.True(open.IsConfigured);
        Assert.True(open.CredentialsPaired); // a localhost relay legitimately has neither

        var authed = new EmailOptions
        {
            SmtpHost = "smtp.example.test",
            From = "noreply@example.test",
            SmtpUser = "noreply@example.test",
            SmtpPassword = "app-password",
        };
        Assert.True(authed.IsConfigured);
        Assert.True(authed.CredentialsPaired);
    }

    [Theory]
    [InlineData("smtp.example.test", null)] // host without from
    [InlineData(null, "noreply@example.test")] // from without host
    public void Half_a_configuration_is_neither_configured_nor_absent(string? host, string? from)
    {
        // The exact shape Program.cs's first Validate refuses: IsConfigured || IsWhollyAbsent
        // is false, so the app won't boot on a typo'd deploy.
        var o = new EmailOptions { SmtpHost = host, From = from };
        Assert.False(o.IsConfigured);
        Assert.False(o.IsWhollyAbsent);
    }

    [Theory]
    [InlineData("someone@example.test", null)]
    [InlineData(null, "app-password")]
    public void A_lone_credential_is_unpaired(string? user, string? password)
    {
        var o = new EmailOptions
        {
            SmtpHost = "smtp.example.test",
            From = "noreply@example.test",
            SmtpUser = user,
            SmtpPassword = password,
        };
        Assert.False(o.CredentialsPaired);
    }
}
