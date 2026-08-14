using System.Security.Claims;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>The one definition of "who is the admin" — every gate in the app (routed-page policy,
/// footer AuthorizeView, reader defense-in-depth) resolves through IsAdmin, so its truth table is
/// the whole feature's access control.</summary>
public class AdminOptionsTests
{
    private static ClaimsPrincipal SignedIn(string? name) => new(new ClaimsIdentity(
        name is null ? [] : [new Claim(ClaimTypes.Name, name)], authenticationType: "test"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    [Fact]
    public void The_configured_email_is_admin_case_insensitively_and_trimmed()
    {
        var options = new AdminOptions { Emails = [" Jordan@Example.com "] };
        Assert.True(options.IsAdmin(SignedIn("jordan@example.com")));
        Assert.True(options.IsAdmin(SignedIn("JORDAN@EXAMPLE.COM")));
    }

    [Fact]
    public void Everyone_else_is_not_admin()
    {
        var options = new AdminOptions { Emails = ["jordan@example.com"] };
        Assert.False(options.IsAdmin(SignedIn("wife@example.com")));
        // Substrings and prefixes must not sneak past — the match is the whole address.
        Assert.False(options.IsAdmin(SignedIn("jordan@example.com.evil.net")));
    }

    [Fact]
    public void An_unauthenticated_or_nameless_principal_is_never_admin()
    {
        var options = new AdminOptions { Emails = ["jordan@example.com"] };
        Assert.False(options.IsAdmin(Anonymous()));
        Assert.False(options.IsAdmin(SignedIn(null)));
    }

    [Fact]
    public void An_empty_section_means_no_admin_exists_anywhere()
    {
        var options = new AdminOptions();
        Assert.False(options.Configured);
        Assert.False(options.IsAdmin(SignedIn("anyone@example.com")));

        // A whitespace-only entry is residue, not a configuration.
        Assert.False(new AdminOptions { Emails = ["  "] }.Configured);
        Assert.True(new AdminOptions { Emails = ["jordan@example.com"] }.Configured);
    }
}
