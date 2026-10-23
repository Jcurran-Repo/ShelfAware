using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
    [InlineData("/list", "Meals")]
    [InlineData("/meal-plan", "Meals")]
    [InlineData("/journal", "Meals")]
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

        nav.NavigateTo("/settings");
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
    public void A_page_link_is_lit_only_on_its_own_page()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cookbook = SiteNav.Pages.Single(p => p.Href == "/cookbook");
        nav.NavigateTo("/cookbook/import");

        var cut = Render<SiteNavLink>(p => p.Add(l => l.Page, cookbook));

        var link = cut.Find("a");
        Assert.Equal("/cookbook", link.GetAttribute("href"));
        Assert.Equal("Cookbook", link.TextContent.Trim());
        Assert.Contains("active", link.ClassList);
        Assert.Equal("page", link.GetAttribute("aria-current"));

        // Its neighbour in the same section is NOT this page: the link goes dark.
        nav.NavigateTo("/recipes");
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("active", cut.Find("a").ClassList);
            Assert.False(cut.Find("a").HasAttribute("aria-current"));
        });
    }

    [Fact]
    public void A_menu_group_is_open_on_its_own_pages_and_follows_the_visitor()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var meals = SiteNav.Sections.Single(s => s.Label == "Meals");
        nav.NavigateTo("/journal");

        var cut = Render<NavGroup>(p => p.Add(g => g.Section, meals));

        Assert.True(cut.Find("details").HasAttribute("open"));
        Assert.Equal("Meals", cut.Find("summary > span:not(.visually-hidden)").TextContent);
        Assert.Contains("here", cut.Find("summary").ClassList);
        // The collapsed-or-not summary tells a screen reader the current page is inside it.
        Assert.Equal(", current section", cut.Find("summary .visually-hidden").TextContent);
        Assert.Equal(new[] { "Grocery List", "Meal Plan", "Journal" },
            cut.FindAll("details a").Select(a => a.TextContent.Trim()));

        nav.NavigateTo("/trends");
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find("details").HasAttribute("open"));
            Assert.DoesNotContain("here", cut.Find("summary").ClassList);
            Assert.Empty(cut.FindAll("summary .visually-hidden"));
        });

        nav.NavigateTo("/list");
        cut.WaitForAssertion(() => Assert.True(cut.Find("details").HasAttribute("open")));
    }

    [Fact]
    public void A_menu_group_starts_closed_away_from_its_pages()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings");
        var insights = SiteNav.Sections.Single(s => s.Label == "Insights");

        var cut = Render<NavGroup>(p => p.Add(g => g.Section, insights));

        Assert.False(cut.Find("details").HasAttribute("open"));
    }

    [Fact]
    public void Toggling_a_menu_group_does_not_reach_the_rail_click_that_folds_the_phone_drawer()
    {
        var meals = SiteNav.Sections.Single(s => s.Label == "Meals");
        var railClicks = 0;
        // The rail's <nav @onclick> closes the drawer; stand in for it with a counting parent.
        var cut = Render(b =>
        {
            b.OpenElement(0, "div");
            b.AddAttribute(1, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => railClicks++));
            b.OpenComponent<NavGroup>(2);
            b.AddAttribute(3, nameof(NavGroup.Section), meals);
            b.CloseComponent();
            b.CloseElement();
        });

        // The summary has no click handler of its own — the browser toggles <details> — so with the
        // bubble stopped there is nothing left for the click to run, which bUnit reports by throwing.
        // Without the stop it would bubble to the parent and count.
        Assert.Throws<MissingEventHandlerException>(() => cut.Find("summary").Click());
        Assert.Equal(0, railClicks);

        // A page link inside the group still does bubble, so tapping one folds the drawer as before.
        cut.Find("details a").Click();
        Assert.Equal(1, railClicks);
    }
}
