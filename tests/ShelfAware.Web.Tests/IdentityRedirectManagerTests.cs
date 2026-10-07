using ShelfAware.Web.Components.Account;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The one reading of "may a ReturnUrl be followed?" (<see cref="IdentityRedirectManager.SafeTarget"/>).
/// The Blazor Identity template guards with <c>Uri.IsWellFormedUriString(uri, UriKind.Relative)</c>, which
/// a protocol-relative <c>//evil.example</c> passes — the browser then resolves it off the site, so a
/// shared sign-in link could land a freshly signed-in tester on another host. These pin the shapes that
/// are refused, the ones that are kept, and that a tampered absolute URL answers with the root rather
/// than the <see cref="ArgumentException"/> the template threw.
/// </summary>
public class IdentityRedirectManagerTests
{
    private const string Base = "https://demo.shelfaware.net/";

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("//evil.example/Account/Login")]
    [InlineData(@"/\evil.example")]
    [InlineData(@"\\evil.example")]
    [InlineData(@"\/evil.example")]
    [InlineData("  //evil.example")] // leading whitespace must not disguise it
    public void A_protocol_relative_target_goes_to_the_root(string tampered) =>
        Assert.Equal("", IdentityRedirectManager.SafeTarget(tampered, Base));

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("http://demo.shelfaware.net/")] // same host, wrong scheme — not under the base either
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.test")]
    public void An_absolute_target_that_is_not_this_site_goes_to_the_root(string foreign) =>
        Assert.Equal("", IdentityRedirectManager.SafeTarget(foreign, Base));

    [Fact]
    public void An_absolute_target_under_the_base_becomes_base_relative()
    {
        // The template's own behaviour for a same-site absolute URL, kept: NavigateTo wants it relative.
        Assert.Equal("products?tag=dairy",
            IdentityRedirectManager.SafeTarget("https://demo.shelfaware.net/products?tag=dairy", Base));
    }

    [Theory]
    [InlineData("/products?tag=dairy", "/products?tag=dairy")]
    [InlineData("/", "/")]
    [InlineData("Account/Login", "Account/Login")]
    [InlineData("Account/Manage?returnUrl=%2Fsettings", "Account/Manage?returnUrl=%2Fsettings")]
    public void A_same_site_path_is_kept_as_is(string local, string expected) =>
        Assert.Equal(expected, IdentityRedirectManager.SafeTarget(local, Base));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_means_the_root(string? empty) =>
        Assert.Equal("", IdentityRedirectManager.SafeTarget(empty, Base));
}
