using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Components.Layout;

namespace ShelfAware.Web.UI.Tests;

/// <summary>The problem-reporting entry points: the footer exists only where an admin exists, the
/// report link carries where the visitor currently is, and the Admin link shows only to the
/// configured admin (via the Admin policy).</summary>
public class SiteFooterTests : PageTestContext
{
    private readonly AdminOptions adminOptions = new() { Emails = ["jordan@test.local"] };
    private BunitAuthorizationContext auth = null!;

    protected override void RegisterAdditionalServices()
    {
        auth = this.AddAuthorization();
        auth.SetAuthorized("wife@test.local");
        Services.AddSingleton(Options.Create(adminOptions));
    }

    [Fact]
    public void The_report_link_carries_the_page_the_visitor_is_on()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/products");
        var cut = Render<SiteFooter>();

        var link = cut.FindAll("a").Single(a => a.TextContent.Contains("Report a bug"));
        Assert.Equal("/bugs?from=%2Fproducts", link.GetAttribute("href"));
    }

    [Fact]
    public void The_admin_link_shows_only_to_the_configured_admin()
    {
        var cut = Render<SiteFooter>();
        Assert.DoesNotContain(cut.FindAll("a"), a => a.TextContent.Trim() == "Admin");

        // The same footer for the admin (the policy granted): the link appears.
        auth.SetAuthorized("jordan@test.local");
        auth.SetPolicies(AdminOptions.PolicyName);
        cut.Render();
        cut.WaitForAssertion(() =>
            Assert.Contains(cut.FindAll("a"), a => a.TextContent.Trim() == "Admin"));
    }

    [Fact]
    public void Without_a_configured_admin_the_footer_does_not_exist()
    {
        adminOptions.Emails = [];
        var cut = Render<SiteFooter>();
        Assert.Empty(cut.FindAll("footer"));
        Assert.Empty(cut.FindAll("a"));
    }
}
