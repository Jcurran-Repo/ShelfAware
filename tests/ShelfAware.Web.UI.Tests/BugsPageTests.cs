using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>The household's side of bug reporting: filing goes through the ordinary
/// household-scoped context (stamped, filtered), the list shows only this household's reports, the
/// "from" query pre-fills visibly rather than capturing silently, and the whole form stands down
/// when no admin is configured to read what it would send.</summary>
public class BugsPageTests : PageTestContext
{
    // Mutable on purpose: Options.Create hands back the same instance, so a test can reconfigure
    // BEFORE its render without a second test class per configuration.
    private readonly AdminOptions adminOptions = new() { Emails = ["jordan@test.local"] };

    protected override void RegisterAdditionalServices()
    {
        var auth = this.AddAuthorization();
        auth.SetAuthorized("wife@test.local");
        Services.AddSingleton(Options.Create(adminOptions));
    }

    private IRenderedComponent<Bugs> RenderBugs()
    {
        var cut = Render<Bugs>();
        cut.WaitForState(() => cut.FindAll("section.panel").Count > 0);
        return cut;
    }

    [Fact]
    public async Task Filing_a_report_persists_it_stamped_and_lists_it()
    {
        var cut = RenderBugs();

        cut.Find("textarea").Input("The dashboard chart is upside down");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Sent — thank you", cut.Markup);
            var row = Assert.Single(cut.FindAll("tbody tr"));
            Assert.Contains("The dashboard chart is upside down", row.TextContent);
            Assert.Contains("wife@test.local", row.TextContent); // who filed it
        });

        await using var raw = Db.CreateUnscopedContext();
        var report = Assert.Single(await raw.BugReports.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("hh-test", report.HouseholdId); // stamped by the scoped context, not the page
        Assert.Equal("wife@test.local", report.ReportedBy);
    }

    [Fact]
    public void Only_this_households_reports_render()
    {
        using (var raw = Db.CreateUnscopedContext())
        {
            raw.BugReports.Add(new BugReport
            {
                HouseholdId = "hh-other", Body = "Another family's complaint", CreatedAt = DateTimeOffset.Now,
            });
            raw.BugReports.Add(new BugReport
            {
                HouseholdId = "hh-test", Body = "Our own report", CreatedAt = DateTimeOffset.Now,
            });
            raw.SaveChanges();
        }

        var cut = RenderBugs();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Our own report", cut.Markup);
            Assert.DoesNotContain("Another family's complaint", cut.Markup);
        });
    }

    [Fact]
    public async Task A_blank_report_is_refused_with_a_reason()
    {
        var cut = RenderBugs();

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
            Assert.Contains("Say what looked wrong first", cut.Find(".error").TextContent));
        await using var raw = Db.CreateUnscopedContext();
        Assert.Empty(await raw.BugReports.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public void The_from_query_pre_fills_where_but_only_for_a_relative_path()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/bugs?from=%2Fproducts");
        var cut = RenderBugs();
        Assert.Equal("/products", cut.Find("input[aria-label='Where it happened']").GetAttribute("value"));
    }

    [Fact]
    public void An_absolute_from_url_is_ignored_not_rendered_into_the_field()
    {
        // The query string is attacker-writable (a pasted link); only an app-relative path may
        // pre-fill the visible field.
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/bugs?from=" + Uri.EscapeDataString("https://evil.example/phish"));
        var cut = RenderBugs();
        Assert.Equal("", cut.Find("input[aria-label='Where it happened']").GetAttribute("value") ?? "");
    }

    [Fact]
    public async Task Overlong_input_is_clamped_server_side_not_just_by_the_attribute()
    {
        var cut = RenderBugs();

        cut.Find("textarea").Input(new string('x', 5000)); // bUnit bypasses maxlength, like devtools would
        cut.Find("input[aria-label='Where it happened']").Change("/" + new string('y', 400));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Sent — thank you", cut.Markup));
        await using var raw = Db.CreateUnscopedContext();
        var report = Assert.Single(await raw.BugReports.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(4000, report.Body.Length);
        Assert.Equal(300, report.PageUrl!.Length);
    }

    [Fact]
    public void A_protocol_relative_from_is_refused_like_any_other_non_app_path()
    {
        // "//host" is protocol-relative (a real URL to another origin), "/\" its backslash twin —
        // neither is the app-relative path the comment promises.
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/bugs?from=" + Uri.EscapeDataString("//evil.example/phish"));
        var cut = RenderBugs();
        Assert.Equal("", cut.Find("input[aria-label='Where it happened']").GetAttribute("value") ?? "");
    }

    [Fact]
    public void A_backslash_relative_from_is_refused_too()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/bugs?from=" + Uri.EscapeDataString(@"/\evil.example"));
        var cut = RenderBugs();
        Assert.Equal("", cut.Find("input[aria-label='Where it happened']").GetAttribute("value") ?? "");
    }

    [Fact]
    public async Task A_reload_failure_after_a_successful_save_never_says_couldnt_save()
    {
        // Two failure points, opposite advice (the item-27 rule): the save landed, so the message
        // must not invite re-sending a report that was recorded.
        var cut = RenderBugs();
        cut.Find("textarea").Input("The chart is upside down");

        Factory.FailAfter = 1; // the save's context succeeds; the reload's fails
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Sent — thank you", cut.Markup);
            Assert.Contains("couldn't refresh", cut.Markup);
            Assert.DoesNotContain("Couldn't save", cut.Markup);
        });
        await using var raw = Db.CreateUnscopedContext();
        Assert.Single(await raw.BugReports.IgnoreQueryFilters().ToListAsync()); // it WAS saved
    }

    [Fact]
    public void A_failing_initial_load_reports_it_instead_of_crashing_the_page()
    {
        Factory.FailAfter = 0;
        var cut = RenderBugs();
        cut.WaitForAssertion(() =>
            Assert.Contains("Couldn't load your reports just now", cut.Markup));
    }

    [Fact]
    public void Without_a_configured_admin_the_form_stands_down_and_says_why()
    {
        adminOptions.Emails = [];
        var cut = RenderBugs();

        Assert.Empty(cut.FindAll("form"));
        Assert.Contains("doesn't have an admin set up to read bug reports", cut.Markup);
        // The household's own history still renders — past reports stay theirs to see.
        Assert.Contains("Your household's reports", cut.Markup);
    }
}
