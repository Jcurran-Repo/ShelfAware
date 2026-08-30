using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Components.Layout;
using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.UI.Tests;

/// <summary>The footer entry points: it ALWAYS renders the public /about link (a page every visitor
/// should reach), while the problem-reporting + Admin links stand down where no admin exists. The
/// report link carries where the visitor currently is, and the Admin link shows only to the configured
/// admin (via the Admin policy).</summary>
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
    public void The_report_link_carries_the_path_only_never_the_pages_query_string()
    {
        // Found live: on /bugs itself the full URI compounded the from= into itself on every
        // visit ("/bugs?from=%2Fbugs%3Ffrom%3D…"). The pre-fill wants the PAGE, not its state.
        Services.GetRequiredService<NavigationManager>().NavigateTo("/products?tag=Dairy");
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
    public void Without_a_configured_admin_the_footer_keeps_about_but_drops_the_bug_and_admin_links()
    {
        adminOptions.Emails = [];
        var cut = Render<SiteFooter>();
        // The footer always renders — /about is a public page every visitor should be able to reach.
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/about");
        // …but the problem-reporting + Admin entry points stand down with no admin to read them.
        Assert.DoesNotContain(cut.FindAll("a"), a => a.TextContent.Contains("Report a bug"));
        Assert.DoesNotContain(cut.FindAll("a"), a => a.TextContent.Trim() == "Admin");
    }

    [Fact]
    public void The_about_link_is_present_even_with_an_admin_configured()
    {
        // The public /about link coexists with the gated bug/admin links, never replaced by them.
        var cut = Render<SiteFooter>();
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/about");
    }

    [Fact]
    public void Clicking_report_captures_the_page_snapshot_and_hands_it_to_bugs()
    {
        var snapshot = new BugReportSnapshot(
            new BugDiagnostics("/products", "800x600 @2x", "UA", "dark", false, "time", "tz", ["boom"]),
            "Milk\nEggs");
        JSInterop.Setup<BugReportSnapshot>("shelfawareBugCapture.snapshot").SetResult(snapshot);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/products");
        var cut = Render<SiteFooter>();

        cut.FindAll("a").Single(a => a.TextContent.Contains("Report a bug")).Click();

        cut.WaitForState(() => nav.Uri.Contains("/bugs"));
        Assert.EndsWith("/bugs?from=%2Fproducts", nav.Uri);
        // The snapshot was stashed for /bugs to collect — captured HERE, on the page the reporter was on.
        var pending = BugContext.TakePending();
        Assert.NotNull(pending);
        Assert.Equal("Milk\nEggs", pending!.PageContent);
        Assert.Equal("/products", pending.Diagnostics?.Url);
    }

    [Fact]
    public void Clicking_report_still_navigates_when_capture_fails()
    {
        // Capture must never block filing the report: a JS failure (or a disconnecting circuit) just means
        // we navigate with no snapshot — the href's from= pre-fill still gets the form there.
        JSInterop.Setup<BugReportSnapshot>("shelfawareBugCapture.snapshot").SetException(new JSException("boom"));
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/products");
        var cut = Render<SiteFooter>();

        cut.FindAll("a").Single(a => a.TextContent.Contains("Report a bug")).Click();

        cut.WaitForState(() => nav.Uri.Contains("/bugs"));
        Assert.EndsWith("/bugs?from=%2Fproducts", nav.Uri);
        Assert.Null(BugContext.TakePending()); // nothing stashed
    }
}
