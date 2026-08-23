using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Diagnostics;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>The admin surface: errors and every household's bug reports on one page, refused
/// inside the component for anyone but the configured admin (a directly-rendered component
/// bypasses routing authorization, which is exactly why the in-component gate exists and exactly
/// what this suite can pin).</summary>
public class AdminPageTests : PageTestContext
{
    private const string AdminEmail = "jordan@test.local";

    private readonly TestAuthDb authDb = new();
    private BunitAuthorizationContext auth = null!;
    private ErrorLogSink sink = null!;
    private OnlinePresence presence = null!;

    protected override void RegisterAdditionalServices()
    {
        auth = this.AddAuthorization();
        auth.SetAuthorized(AdminEmail);
        sink = new ErrorLogSink();
        presence = new OnlinePresence();
        Services.AddSingleton(Options.Create(new AdminOptions { Emails = [AdminEmail] }));
        Services.AddSingleton(sink);
        Services.AddSingleton(presence);
        Services.AddSingleton<IDbContextFactory<AuthDbContext>>(authDb);
        Services.AddSingleton(new ErrorLogStore(authDb));
        Services.AddSingleton<LoginAudit>();
        Services.AddScoped<AdminReportReader>();
        Services.AddScoped<ReportResolutionService>();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) authDb.Dispose();
    }

    private void SeedReport(string household, string body)
    {
        Db.HouseholdId = household;
        using var db = Db.CreateDbContext();
        db.BugReports.Add(new BugReport { Body = body, CreatedAt = DateTimeOffset.Now });
        db.SaveChanges();
        Db.HouseholdId = "hh-test";
    }

    [Fact]
    public async Task The_admin_sees_errors_and_every_households_reports()
    {
        using (var db = authDb.CreateDbContext())
        {
            db.Households.Add(new Household { Id = "hh-a", Name = "The Currans" });
            db.Households.Add(new Household { Id = "hh-b", Name = "The Neighbours" });
            db.SaveChanges();
        }
        SeedReport("hh-a", "The chart looks wrong");
        SeedReport("hh-b", "The list printed sideways");
        await new ErrorLogStore(authDb).RecordAsync(new CapturedError(
            DateTimeOffset.Now, "Error", "ShelfAware.Web.Components.Pages.Home",
            "System.InvalidOperationException", "Loading failed", "Loading failed", "stack detail"));

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Loading failed", cut.Markup);
            Assert.Contains("The chart looks wrong", cut.Markup);
            Assert.Contains("The Currans", cut.Markup);
            Assert.Contains("The list printed sideways", cut.Markup);
            Assert.Contains("The Neighbours", cut.Markup);
        });
    }

    [Fact]
    public void A_non_admin_is_refused_by_the_component_itself()
    {
        // Same signed-in shape, wrong person: the routed policy would already bounce them, but a
        // component rendered directly must refuse on its own.
        auth.SetAuthorized("wife@test.local");
        SeedReport("hh-a", "Should never render");

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
            Assert.Contains("only for the configured admin", cut.Find(".error").TextContent));
        Assert.DoesNotContain("Should never render", cut.Markup);
        Assert.Empty(cut.FindAll("table"));
    }

    [Fact]
    public void Dropped_events_are_disclosed_not_hidden()
    {
        sink.CountDrop();

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() => Assert.Contains("1 event dropped under load", cut.Markup));
    }

    [Fact]
    public void The_report_cap_is_disclosed_when_hit()
    {
        // No silent caps: when the reader returns exactly its bound, the page says so.
        Db.HouseholdId = "hh-a";
        using (var db = Db.CreateDbContext())
        {
            for (var i = 0; i < AdminReportReader.MaxReports; i++)
            {
                db.BugReports.Add(new BugReport { Body = $"r{i}", CreatedAt = DateTimeOffset.Now });
            }
            db.SaveChanges();
        }
        Db.HouseholdId = "hh-test";

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
            Assert.Contains($"Showing {AdminReportReader.MaxReports} reports, open ones first", cut.Markup));
    }

    [Fact]
    public void Open_reports_survive_the_cap_that_resolved_ones_fill()
    {
        // The cap is spent open-first: without that ordering, a diligent admin's resolved pile
        // pushed older OPEN reports out of the window entirely, while the page said "Nothing open".
        Db.HouseholdId = "hh-a";
        using (var db = Db.CreateDbContext())
        {
            db.BugReports.Add(new BugReport
            {
                Body = "the old open one", CreatedAt = DateTimeOffset.Now.AddDays(-30),
            });
            for (var i = 0; i < AdminReportReader.MaxReports; i++)
            {
                db.BugReports.Add(new BugReport
                {
                    Body = $"resolved {i}", CreatedAt = DateTimeOffset.Now, ResolvedAt = DateTimeOffset.Now,
                });
            }
            db.SaveChanges();
        }
        Db.HouseholdId = "hh-test";

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("the old open one", cut.Markup); // in the window despite 500 newer resolved rows
            Assert.DoesNotContain("Nothing open — every report is marked resolved.", cut.Markup);
        });
    }

    [Fact]
    public async Task A_recurrence_between_render_and_click_is_not_swallowed_by_the_resolve()
    {
        // The Resolve button stamps the LastSeenAt the admin was LOOKING AT, so an occurrence that
        // fired after their render lands past the mark and keeps the row open, wearing the
        // recurred note. Stamping the click's clock here instead swallowed it silently.
        var store = new ErrorLogStore(authDb);
        await store.RecordAsync(new CapturedError(
            DateTimeOffset.Now.AddHours(-1), "Error", "ShelfAware.Web.Components.Pages.Home",
            "System.InvalidOperationException", "Loading failed", "Loading failed", "detail"));
        var cut = Render<Components.Pages.Admin>();
        cut.WaitForAssertion(() => Assert.Contains("Loading failed", cut.Markup));

        await store.RecordAsync(new CapturedError(
            DateTimeOffset.Now, "Error", "ShelfAware.Web.Components.Pages.Home",
            "System.InvalidOperationException", "Loading failed", "Loading failed", "detail"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Resolve").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("seen again after being resolved", cut.Markup); // still OPEN, with the news
            Assert.Contains("Resolve", cut.FindAll("button").Select(b => b.TextContent.Trim()));
        });
    }

    [Fact]
    public async Task A_refresh_failure_after_a_successful_resolve_says_saved_not_try_again()
    {
        // Two failure points, opposite advice (item 27): the write landed, so the message must
        // lead with that — a resolved row rendering as open beside "try again" invites a second
        // resolve of work that's already done.
        using (var db = authDb.CreateDbContext())
        {
            db.Households.Add(new Household { Id = "hh-a", Name = "The Currans" });
            db.SaveChanges();
        }
        SeedReport("hh-a", "The chart looks wrong");
        var cut = Render<Components.Pages.Admin>();
        cut.WaitForAssertion(() => Assert.Contains("The chart looks wrong", cut.Markup));

        Factory.FailAfter = 1; // the resolve's write context succeeds; the refresh's re-read fails
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Resolve").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Saved — but the lists couldn't refresh just now", cut.Markup);
            Assert.DoesNotContain("nothing changed", cut.Markup);
        });
        await using var raw = Db.CreateUnscopedContext();
        Assert.NotNull((await raw.BugReports.IgnoreQueryFilters().SingleAsync()).ResolvedAt); // it WAS saved
    }

    [Fact]
    public void Quiet_states_say_so_instead_of_rendering_nothing()
    {
        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Nobody's connected right now.", cut.Markup);
            Assert.Contains("No logins recorded yet.", cut.Markup);
            Assert.Contains("Nothing logged — quiet so far.", cut.Markup);
            Assert.Contains("No reports yet.", cut.Markup);
        });
    }

    // ------------------------------------------------------------------- logins + presence

    [Fact]
    public async Task The_admin_sees_the_login_stats_with_a_total()
    {
        using (var db = authDb.CreateDbContext())
        {
            db.UserLoginStats.Add(new UserLoginStat
            {
                UserId = "u1", Email = "jordan@test.local", LoginCount = 3,
                FirstLoginAt = DateTimeOffset.Now.AddDays(-10), LastLoginAt = DateTimeOffset.Now,
            });
            db.UserLoginStats.Add(new UserLoginStat
            {
                UserId = "u2", Email = "wife@test.local", LoginCount = 2,
                FirstLoginAt = DateTimeOffset.Now.AddDays(-5), LastLoginAt = DateTimeOffset.Now.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("jordan@test.local", cut.Markup);
            Assert.Contains("wife@test.local", cut.Markup);
            // The total is the SUM (3 + 2), across the two accounts. Asserted in parts because the
            // sentence wraps in the source, so the raw markup carries a newline where the browser shows
            // a space (HTML collapses it).
            Assert.Contains("5 total sign-in", cut.Markup);
            Assert.Contains("2 account", cut.Markup);
        });
    }

    [Fact]
    public void The_online_now_section_reflects_live_presence()
    {
        // The singleton the page reads is the one this suite registered — populate it directly (no
        // circuits exist under bUnit) and the page shows who's on.
        presence.Connect("c1", new OnlineUser("u1", "jordan@test.local"), DateTimeOffset.Now);
        presence.Connect("c2", new OnlineUser("u2", "wife@test.local"), DateTimeOffset.Now);

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Online now (2)", cut.Markup);
            Assert.Contains("jordan@test.local", cut.Markup);
            Assert.Contains("wife@test.local", cut.Markup);
            Assert.DoesNotContain("Nobody's connected right now.", cut.Markup);
        });
    }

    [Fact]
    public void The_online_now_section_updates_live_after_a_connection_arrives_or_leaves()
    {
        // Rendered with nobody on — the initial snapshot is empty.
        var cut = Render<Components.Pages.Admin>();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Online now (0)", cut.Markup);
            Assert.Contains("Nobody's connected right now.", cut.Markup);
        });

        // A connection arrives on ANOTHER circuit after the page is already up. The page must re-render
        // live off the Changed subscription (OnPresenceChanged marshals StateHasChanged) — not only from
        // the first snapshot. Removing the `Presence.Changed += OnPresenceChanged` wiring fails this.
        presence.Connect("c1", new OnlineUser("u1", "newcomer@test.local"), DateTimeOffset.Now);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Online now (1)", cut.Markup);
            Assert.Contains("newcomer@test.local", cut.Markup);
        });

        // …and it empties live when they leave.
        presence.Disconnect("c1");
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Online now (0)", cut.Markup);
            Assert.Contains("Nobody's connected right now.", cut.Markup);
        });
    }

    // ------------------------------------------------------------------- resolve / reopen

    [Fact]
    public async Task Resolving_a_report_moves_it_to_the_resolved_list_and_reopening_brings_it_back()
    {
        using (var db = authDb.CreateDbContext())
        {
            db.Households.Add(new Household { Id = "hh-a", Name = "The Currans" });
            db.SaveChanges();
        }
        SeedReport("hh-a", "The chart looks wrong");
        var cut = Render<Components.Pages.Admin>();
        cut.WaitForAssertion(() => Assert.Contains("The chart looks wrong", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Resolve").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Nothing open — every report is marked resolved.", cut.Markup);
            Assert.Contains("Resolved (1)", cut.Markup);
        });
        // The write really landed on the foreign household's row (the admin's own scope is hh-test).
        await cut.WaitForAssertionAsync(async () =>
        {
            await using var raw = Db.CreateUnscopedContext();
            var report = await raw.BugReports.IgnoreQueryFilters().SingleAsync();
            Assert.NotNull(report.ResolvedAt);
            Assert.Equal("hh-a", report.HouseholdId);
        });

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reopen").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Resolved (1)", cut.Markup);
            Assert.Contains("The chart looks wrong", cut.Markup);
        });
    }

    [Fact]
    public async Task A_resolved_error_leaves_the_open_list_and_returns_when_it_recurs()
    {
        var store = new ErrorLogStore(authDb);
        await store.RecordAsync(new CapturedError(
            DateTimeOffset.Now.AddHours(-1), "Error", "ShelfAware.Web.Components.Pages.Home",
            "System.InvalidOperationException", "Loading failed", "Loading failed", "detail"));
        var cut = Render<Components.Pages.Admin>();
        cut.WaitForAssertion(() => Assert.Contains("Loading failed", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Resolve").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Nothing open — every logged error is marked handled.", cut.Markup);
            Assert.Contains("Resolved (1)", cut.Markup);
        });

        // The same fingerprint fires again. The capture pipeline knows nothing about resolution —
        // the row must be back in the open list on the next look purely by derivation, wearing the
        // recurred-after-resolve note rather than looking like something new.
        await store.RecordAsync(new CapturedError(
            DateTimeOffset.Now, "Error", "ShelfAware.Web.Components.Pages.Home",
            "System.InvalidOperationException", "Loading failed", "Loading failed", "detail"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Refresh").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("seen again after being resolved", cut.Markup);
            Assert.DoesNotContain("Resolved (1)", cut.Markup);
        });
    }
}
