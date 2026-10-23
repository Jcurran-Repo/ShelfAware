using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Web.Components.Layout;

namespace ShelfAware.Web.UI.Tests;

/// <summary>SiteNav ties the rail, the section tab strip and the bug form's "where" dropdown to one
/// page list — but nothing ties the LIST to the app's real routes, and no harness renders MainLayout's
/// half. This pins the side that can rot silently: a renamed route would leave every consumer
/// confidently agreeing on a 404. It also pins the one rule for "which page am I on", which the rail
/// and the tabs both ask.</summary>
public class SiteNavTests : BunitContext
{
    [Fact]
    public void Every_nav_href_is_a_declared_page_route()
    {
        var routes = typeof(SiteNav).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false)
                .Cast<RouteAttribute>())
            .Select(r => r.Template)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in SiteNav.Pages)
        {
            Assert.Contains(entry.Href, routes);
        }
    }

    [Fact]
    public void Every_page_belongs_to_exactly_one_section()
    {
        Assert.Equal(SiteNav.Pages.Count, SiteNav.Pages.Select(p => p.Href).Distinct().Count());
        foreach (var entry in SiteNav.Pages)
        {
            Assert.Single(SiteNav.Sections, s => s.Contains(entry.Href));
        }
    }

    [Theory]
    [InlineData("/", "Dashboard")]
    [InlineData("/receipt", "Receipts")]
    [InlineData("/receipts", "Receipts")]
    [InlineData("/pantry-photo", "Receipts")]
    [InlineData("/cookbook", "Recipes")]
    [InlineData("/cookbook/import", "Recipes")]
    [InlineData("/history", "Products")]
    [InlineData("/accuracy", "Insights")]
    [InlineData("/Trends", "Insights")]
    public void A_path_is_in_the_section_that_holds_its_page(string path, string section)
    {
        Assert.Equal(section, SiteNav.SectionAt(path)?.Label);
    }

    [Theory]
    [InlineData("/bugs")]
    [InlineData("/admin")]
    [InlineData("/product/12")]
    [InlineData("/receipt-old")]
    public void A_path_outside_the_menu_is_in_no_section(string path)
    {
        Assert.Null(SiteNav.SectionAt(path));
    }

    [Fact]
    public void Matching_is_segment_aware()
    {
        var upload = SiteNav.Pages.Single(p => p.Href == "/receipt");
        Assert.True(SiteNav.IsAt(upload, "/receipt"));
        Assert.False(SiteNav.IsAt(upload, "/receipts"));

        var dashboard = SiteNav.Pages.Single(p => p.Href == "/");
        Assert.True(SiteNav.IsAt(dashboard, "/"));
        Assert.False(SiteNav.IsAt(dashboard, "/list"));
    }

    [Theory]
    [InlineData("http://localhost/", "/")]
    [InlineData("http://localhost/cookbook/import?x=1", "/cookbook/import")]
    [InlineData("http://localhost/trends#prices", "/trends")]
    public void The_path_drops_the_query_and_fragment(string uri, string path)
    {
        Assert.Equal(path, SiteNav.PathOf(Services.GetRequiredService<NavigationManager>(), uri));
    }

    [Fact]
    public void A_section_with_several_pages_shows_them_as_tabs_and_marks_the_current_one()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cookbook/import");

        var cut = Render<SectionTabs>();

        var tabs = cut.FindAll(".section-tabs a");
        Assert.Equal(new[] { "Recipes", "Cookbook" }, tabs.Select(a => a.TextContent.Trim()));
        Assert.Equal("Recipes", cut.Find(".section-tabs").GetAttribute("aria-label"));
        var current = Assert.Single(tabs, a => a.GetAttribute("aria-current") == "page");
        Assert.Equal("/cookbook", current.GetAttribute("href"));
    }

    [Fact]
    public void The_tabs_follow_navigation_into_another_section_and_out_of_the_menu()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/recipes");
        var cut = Render<SectionTabs>();

        nav.NavigateTo("/reports");
        cut.WaitForAssertion(() => Assert.Equal(
            new[] { "Trends", "Reports", "Accuracy" },
            cut.FindAll(".section-tabs a").Select(a => a.TextContent.Trim())));

        nav.NavigateTo("/list");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".section-tabs")));
    }

    [Fact]
    public void A_single_page_section_shows_no_tabs()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings");

        var cut = Render<SectionTabs>();

        Assert.Empty(cut.FindAll(".section-tabs"));
    }

    [Fact]
    public void A_section_link_stays_lit_on_every_page_of_the_section()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var receipts = SiteNav.Sections.Single(s => s.Label == "Receipts");
        nav.NavigateTo("/pantry-photo");

        var cut = Render<SiteNavLink>(p => p.Add(l => l.Page, receipts.Home).Add(l => l.Section, receipts));

        var link = cut.Find("a");
        Assert.Equal("/receipts", link.GetAttribute("href"));
        Assert.Equal("Receipts", link.TextContent.Trim());
        Assert.Contains("active", link.ClassList);
        // Lit, but it points at a different page than this one: a location, not the current page.
        Assert.Equal("true", link.GetAttribute("aria-current"));

        nav.NavigateTo("/receipts");
        cut.WaitForAssertion(() => Assert.Equal("page", cut.Find("a").GetAttribute("aria-current")));

        nav.NavigateTo("/products");
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("active", cut.Find("a").ClassList);
            Assert.False(cut.Find("a").HasAttribute("aria-current"));
        });
    }
}
