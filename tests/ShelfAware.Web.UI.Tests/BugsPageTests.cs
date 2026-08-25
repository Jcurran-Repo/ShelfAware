using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Components.Layout;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;

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
        Services.AddScoped<ReporterReportService>(); // the reporter's own resolve/reopen path
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

    /// <summary>The select's value, and the way into the escape hatch — found by its visible text
    /// rather than the sentinel value, so a sentinel rename can't quietly turn these vacuous.</summary>
    private static AngleSharp.Dom.IElement WhereSelect(IRenderedComponent<Bugs> cut) =>
        cut.Find("select[aria-label='Which page it happened on']");

    private static void PickSomewhereElse(IRenderedComponent<Bugs> cut)
    {
        var select = WhereSelect(cut);
        var value = select.QuerySelectorAll("option")
            .Single(o => o.TextContent.Contains("Somewhere else")).GetAttribute("value");
        select.Change(value);
    }

    [Fact]
    public void The_where_choices_are_the_site_nav_plus_an_escape_hatch()
    {
        // ONE list (SiteNav): the header renders it and this dropdown renders it, so a new page
        // shows up in both or neither — the choices can never drift from the app's real pages.
        var cut = RenderBugs();

        var options = WhereSelect(cut).QuerySelectorAll("option")
            .Select(o => o.TextContent.Trim()).ToList();

        Assert.Equal(
            new[] { "—" }.Concat(SiteNav.Pages.Select(p => p.Label)).Append("Somewhere else…").ToList(),
            options);
    }

    [Fact]
    public void A_from_path_the_menu_knows_preselects_its_page()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/bugs?from=%2Fproducts");
        var cut = RenderBugs();

        Assert.Equal("/products", WhereSelect(cut).GetAttribute("value"));
        Assert.Empty(cut.FindAll("input[aria-label='Where it happened']")); // no escape hatch needed
    }

    [Fact]
    public void A_from_path_off_the_menu_keeps_its_exact_path_in_the_escape_hatch()
    {
        // The admin wants /product/12, not "Products" — the footer link's specificity must
        // survive the dropdown.
        Services.GetRequiredService<NavigationManager>().NavigateTo("/bugs?from=%2Fproduct%2F12");
        var cut = RenderBugs();

        Assert.Equal("/product/12", cut.Find("input[aria-label='Where it happened']").GetAttribute("value"));
    }

    [Fact]
    public async Task Picking_a_menu_page_stores_its_path()
    {
        var cut = RenderBugs();
        cut.Find("textarea").Input("The list printed sideways");
        WhereSelect(cut).Change("/list");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Sent — thank you", cut.Markup));
        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal("/list", (await raw.BugReports.IgnoreQueryFilters().SingleAsync()).PageUrl);
    }

    [Fact]
    public void An_absolute_from_url_is_ignored_not_rendered_into_the_form()
    {
        // The query string is attacker-writable (a pasted link); only an app-relative path may
        // pre-fill anything.
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/bugs?from=" + Uri.EscapeDataString("https://evil.example/phish"));
        var cut = RenderBugs();
        Assert.Equal("", WhereSelect(cut).GetAttribute("value") ?? "");
        Assert.Empty(cut.FindAll("input[aria-label='Where it happened']"));
    }

    [Fact]
    public async Task Overlong_input_is_clamped_server_side_not_just_by_the_attribute()
    {
        var cut = RenderBugs();

        cut.Find("textarea").Input(new string('x', 5000)); // bUnit bypasses maxlength, like devtools would
        PickSomewhereElse(cut);
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
        Assert.Equal("", WhereSelect(cut).GetAttribute("value") ?? "");
        Assert.Empty(cut.FindAll("input[aria-label='Where it happened']"));
    }

    [Fact]
    public void A_backslash_relative_from_is_refused_too()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/bugs?from=" + Uri.EscapeDataString(@"/\evil.example"));
        var cut = RenderBugs();
        Assert.Equal("", WhereSelect(cut).GetAttribute("value") ?? "");
        Assert.Empty(cut.FindAll("input[aria-label='Where it happened']"));
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

    [Fact]
    public void A_resolved_report_shows_its_chip_to_the_household_that_filed_it()
    {
        // The loop closed: the admin's resolve is visible to the reporter, so filing a report
        // isn't a one-way letterbox. Their own rows — an ordinary scoped read.
        // ⚠️ The seeded bodies must not CONTAIN the words the status cell renders — an earlier
        // fixture named a row "The open one", so its "open" assert passed with the whole status
        // branch deleted (item 38's cannot-tell-branches-apart class).
        using (var raw = Db.CreateUnscopedContext())
        {
            raw.BugReports.Add(new BugReport
            {
                HouseholdId = "hh-test", Body = "The fixed one", CreatedAt = DateTimeOffset.Now.AddDays(-2),
                ResolvedAt = DateTimeOffset.Now.AddDays(-1),
            });
            raw.BugReports.Add(new BugReport
            {
                HouseholdId = "hh-test", Body = "Still broken here", CreatedAt = DateTimeOffset.Now,
            });
            raw.SaveChanges();
        }

        var cut = RenderBugs();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("tbody tr");
            Assert.Contains("✓ resolved", rows.Single(r => r.TextContent.Contains("The fixed one")).TextContent);
            Assert.Contains("open", rows.Single(r => r.TextContent.Contains("Still broken here")).TextContent);
        });
    }

    // ⚠️ Bodies below avoid the words the status cell renders ("open"/"resolved"/"fixed"/"proposed"),
    // per the cannot-tell-branches-apart lesson noted on the chip test above.

    [Fact]
    public async Task An_open_report_can_be_self_resolved_by_the_reporter()
    {
        using (var raw = Db.CreateUnscopedContext())
        {
            raw.BugReports.Add(new BugReport { HouseholdId = "hh-test", Body = "The chart is sideways", CreatedAt = DateTimeOffset.Now });
            raw.SaveChanges();
        }
        var cut = RenderBugs();
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Mark fixed"));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Mark fixed").Click();

        cut.WaitForAssertion(() => Assert.Contains("✓ resolved", cut.Find("tbody tr").TextContent));
        await using var raw2 = Db.CreateUnscopedContext();
        Assert.NotNull((await raw2.BugReports.IgnoreQueryFilters().SingleAsync()).ResolvedAt);
    }

    [Fact]
    public async Task A_proposed_report_lets_the_reporter_confirm_the_fix()
    {
        using (var raw = Db.CreateUnscopedContext())
        {
            raw.BugReports.Add(new BugReport
            {
                HouseholdId = "hh-test", Body = "The widget misbehaves", CreatedAt = DateTimeOffset.Now.AddDays(-1),
                ProposedResolvedAt = DateTimeOffset.Now, // the admin proposed it
            });
            raw.SaveChanges();
        }
        var cut = RenderBugs();
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Confirm fixed"));
        Assert.Contains("proposed fixed", cut.Find("tbody tr").TextContent);
        Assert.Contains("Still broken", cut.FindAll("button").Select(b => b.TextContent.Trim()));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Confirm fixed").Click();

        cut.WaitForAssertion(() => Assert.Contains("✓ resolved", cut.Find("tbody tr").TextContent));
        await using var raw2 = Db.CreateUnscopedContext();
        Assert.NotNull((await raw2.BugReports.IgnoreQueryFilters().SingleAsync()).ResolvedAt);
    }

    [Fact]
    public async Task Still_broken_returns_a_proposed_report_to_open()
    {
        using (var raw = Db.CreateUnscopedContext())
        {
            raw.BugReports.Add(new BugReport
            {
                HouseholdId = "hh-test", Body = "The widget misbehaves", CreatedAt = DateTimeOffset.Now.AddDays(-1),
                ProposedResolvedAt = DateTimeOffset.Now,
            });
            raw.SaveChanges();
        }
        var cut = RenderBugs();
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Still broken"));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Still broken").Click();

        // Back to open — and the proposal is cleared, so it never lingers as "awaiting reporter".
        cut.WaitForAssertion(() => Assert.Contains("open", cut.Find("tbody tr").TextContent));
        await using var raw2 = Db.CreateUnscopedContext();
        var report = await raw2.BugReports.IgnoreQueryFilters().SingleAsync();
        Assert.Null(report.ResolvedAt);
        Assert.Null(report.ProposedResolvedAt);
    }

    [Fact]
    public async Task A_tampered_dropdown_value_is_not_stored()
    {
        // A select's @bind takes whatever string arrives over the circuit — it does NOT re-validate
        // against the rendered options — so the picked-page arm allowlists against SiteNav, the
        // same list that rendered them. A junk value stores null, never itself.
        var cut = RenderBugs();
        cut.Find("textarea").Input("Legit words");
        WhereSelect(cut).Change("https://evil.example/" + new string('x', 400));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Sent — thank you", cut.Markup));
        await using var raw = Db.CreateUnscopedContext();
        Assert.Null((await raw.BugReports.IgnoreQueryFilters().SingleAsync()).PageUrl);
    }

    [Fact]
    public async Task A_sent_report_resets_the_where_selection_too()
    {
        // A retained dropdown pick reads as a deliberate default — a second, unrelated report typed
        // into the fresh-looking form was silently filed against the previous report's page.
        var cut = RenderBugs();
        cut.Find("textarea").Input("The list printed sideways");
        WhereSelect(cut).Change("/list");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("Sent — thank you", cut.Markup));

        cut.Find("textarea").Input("An unrelated thing about trends");
        cut.Find("form").Submit();

        await cut.WaitForAssertionAsync(async () =>
        {
            await using var raw = Db.CreateUnscopedContext();
            var reports = await raw.BugReports.IgnoreQueryFilters().OrderBy(r => r.Id).ToListAsync();
            Assert.Equal(2, reports.Count);
            Assert.Equal("/list", reports[0].PageUrl);
            Assert.Null(reports[1].PageUrl); // not inherited from the first report
        });
    }
}
