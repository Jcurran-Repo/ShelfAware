using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Evaluation;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Diagnostics;
using ShelfAware.Web.Services;
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
    // The demo box-wide valve's config — unconfigured by default (so the panel is hidden and every other
    // admin test is unaffected). A demo-panel test mutates this BEFORE Render(); Options.Create wraps this
    // same instance, so the change is live at render time.
    private readonly DemoOptions demoOptions = new();

    private sealed class FakeCiStatus : ICiStatusProvider
    {
        public bool Enabled => true;
        public Task<CiStatus> GetAsync(CancellationToken ct = default) => Task.FromResult(new CiStatus(
        [
            new CiRun("CI", "completed", "success", "master", "abc1234def", DateTimeOffset.Now, "https://gh/runs/1"),
            new CiRun("Mutation", "completed", "failure", "master", "abc1234def", DateTimeOffset.Now, "https://gh/runs/2"),
        ], DateTimeOffset.Now, null));
    }

    private sealed class FakeTestStatus : ITestStatusProvider
    {
        public TestStatusReport? Report { get; set; } = new()
        {
            GeneratedAt = DateTimeOffset.Now,
            CommitSha = "abcdef1234567",
            Branch = "master",
            Projects = [new TestProjectResult("Engine", 10, 10, 0, 0), new TestProjectResult("Pages", 5, 5, 0, 0)],
        };
        public TestStatusReport? Read() => Report;
    }

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
        Services.AddSingleton(new ShelfAware.Web.Wishlist.WishlistStore(authDb));
        Services.AddSingleton<LoginAudit>();
        Services.AddScoped<AdminReportReader>();
        Services.AddScoped<ReportResolutionService>();
        Services.AddScoped<AdminHouseholdService>();
        Services.AddScoped<AdminAiSpendReader>();
        Services.AddSingleton<ICiStatusProvider>(new FakeCiStatus());
        Services.AddSingleton<ITestStatusProvider>(new FakeTestStatus());
        Services.AddSingleton<IOptions<DemoOptions>>(Options.Create(demoOptions));
        Services.AddSingleton<DemoUsageMeter>();
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

    private void SeedUsage(string household, DateOnly day, int calls, long cost)
    {
        Db.HouseholdId = household;
        using var db = Db.CreateDbContext();
        db.AiUsages.Add(new AiUsage { Day = day, Calls = calls, CostMicros = cost });
        db.SaveChanges();
        Db.HouseholdId = "hh-test";
    }

    private void SeedActivity(string household, string summary)
    {
        Db.HouseholdId = household;
        using var db = Db.CreateDbContext();
        db.ActivityEntries.Add(new ActivityEntry
        {
            Summary = summary, OccurredAt = DateTimeOffset.Now, Kind = ActivityKind.SignalRecorded,
            Reversibility = Reversibility.Reversible, PayloadJson = "{}",
        });
        db.SaveChanges();
        Db.HouseholdId = "hh-test";
    }

    [Fact]
    public void The_recent_activity_panel_shows_the_audit_trail_across_households()
    {
        using (var db = authDb.CreateDbContext())
        {
            db.Households.Add(new Household { Id = "hh-a", Name = "The Currans" });
            db.SaveChanges();
        }
        SeedActivity("hh-a", "Bought Whole Milk");

        var cut = Render<Components.Pages.Admin>();

        // The audit trail from /history, surfaced on the admin/error page — another household's action,
        // named, so an operator can correlate it with the errors above.
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Recent activity", cut.Markup);
            Assert.Contains("Bought Whole Milk", cut.Markup);
            Assert.Contains("The Currans", cut.Markup);
        });
    }

    [Fact]
    public async Task The_wishlist_panel_shows_the_reserve_list_and_the_distinct_email_count()
    {
        var store = new ShelfAware.Web.Wishlist.WishlistStore(authDb);
        await store.RecordAsync("aware", "jordan@example.com", DateTimeOffset.Now);
        await store.RecordAsync("shelf", null, DateTimeOffset.Now); // an anonymous interest click — counted, no contact

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Hosted wishlist", cut.Markup);
            Assert.Contains("jordan@example.com", cut.Markup); // the notify contact, from the admin-gated read
            Assert.Contains("Reserved email", cut.Markup);     // the distinct-email stat (1 → singular label)
        });
    }

    [Fact]
    public void The_glance_strip_sums_ai_spend_across_every_household()
    {
        using (var db = authDb.CreateDbContext())
        {
            db.Households.Add(new Household { Id = "hh-a", Name = "The Currans" });
            db.Households.Add(new Household { Id = "hh-b", Name = "The Neighbours" });
            db.SaveChanges();
        }
        var today = DateOnly.FromDateTime(DateTime.Today);
        SeedUsage("hh-a", today, calls: 3, cost: 1_500_000);
        SeedUsage("hh-b", today, calls: 2, cost: 500_000);

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("At a glance", cut.Markup);
            // The strip sums BOTH households (the cross-household read), not just the admin's own scope —
            // a scoped read would show 2 calls / 1 active. Asserted culture-independently (the currency
            // symbol varies by the host's locale, per the deploy notes).
            Assert.Contains("5 call", cut.Markup);   // today's calls: hh-a (3) + hh-b (2)
            Assert.Contains("2 active", cut.Markup);  // both households used AI this month
        });
    }

    [Fact]
    public void The_ci_card_shows_the_latest_run_of_each_workflow()
    {
        // The CI status loads after first render (OnAfterRenderAsync) from the fake provider.
        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Continuous integration", cut.Markup);
            Assert.Contains("✓ Passed", cut.Markup);   // CI succeeded → green tile
            Assert.Contains("✗ Failed", cut.Markup);    // Mutation failed → red tile
            Assert.Contains("stat fail", cut.Markup);    // the fail styling is applied
        });
    }

    [Fact]
    public void The_tests_and_quality_card_shows_the_committed_snapshot()
    {
        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("quality", cut.Markup);
            Assert.Contains("15 / 15", cut.Markup);   // TotalPassed / TotalTests across both projects
            Assert.Contains("Engine", cut.Markup);     // the per-project table
            Assert.Contains("Pages", cut.Markup);
        });
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
    public void A_reports_attached_snapshot_renders_in_its_row()
    {
        using (var db = authDb.CreateDbContext())
        {
            db.Households.Add(new Household { Id = "hh-a", Name = "The Currans" });
            db.SaveChanges();
        }
        Db.HouseholdId = "hh-a";
        using (var db = Db.CreateDbContext())
        {
            db.BugReports.Add(new BugReport
            {
                Body = "The dashboard is off",
                CreatedAt = DateTimeOffset.Now,
                StateJson = new BugReportSnapshot(
                    new BugDiagnostics("/dashboard", "800x600 @2x", "UA", "dark (auto)", false, "7pm",
                        "America/New_York", ["TypeError: boom @ x.js:1"]),
                    "on-screen text").Serialize(),
            });
            db.SaveChanges();
        }
        Db.HouseholdId = "hh-test";

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Attached details", cut.Markup);
            Assert.Contains("/dashboard", cut.Markup);
            Assert.Contains("TypeError: boom", cut.Markup);
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

    // ------------------------------------------------------------------- households + Founder

    [Fact]
    public void The_admin_sees_the_household_roster_with_members_and_tier()
    {
        using (var db = authDb.CreateDbContext())
        {
            var currans = new Household { Name = "The Currans", Tier = HouseholdTier.Founder };
            var neighbours = new Household { Name = "The Neighbours" };
            db.Households.AddRange(currans, neighbours);
            db.Users.Add(new AppUser { UserName = "jordan@test.local", Email = "jordan@test.local", HouseholdId = currans.Id });
            db.Users.Add(new AppUser { UserName = "wife@test.local", Email = "wife@test.local", HouseholdId = currans.Id });
            db.SaveChanges();
        }

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("The Currans", cut.Markup);
            Assert.Contains("wife@test.local", cut.Markup);
            Assert.Contains("★ Founder", cut.Markup);      // the Founder household is marked
            Assert.Contains("The Neighbours", cut.Markup);
        });
    }

    [Fact]
    public async Task Granting_founder_flips_the_row_and_removing_reverts_it()
    {
        string id;
        using (var db = authDb.CreateDbContext())
        {
            var household = new Household { Name = "The Currans" };
            db.Households.Add(household);
            db.SaveChanges();
            id = household.Id;
        }
        var cut = Render<Components.Pages.Admin>();
        cut.WaitForAssertion(() => Assert.Contains("Grant Founder", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Grant Founder").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("★ Founder", cut.Markup);                                   // the row's tier flipped
            Assert.Contains("Remove Founder", cut.FindAll("button").Select(b => b.TextContent.Trim())); // and its button
        });
        // The tier really changed in auth.db.
        await cut.WaitForAssertionAsync(async () =>
        {
            await using var db = authDb.CreateDbContext();
            var h = await db.Households.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(HouseholdTier.Founder, h.Tier);
            Assert.NotNull(h.FounderSince);
        });

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove Founder").Click();

        await cut.WaitForAssertionAsync(async () =>
        {
            await using var db = authDb.CreateDbContext();
            var h = await db.Households.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(HouseholdTier.Free, h.Tier);
            Assert.Null(h.FounderSince);
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
    public async Task Proposing_a_bug_hands_it_to_the_reporter_without_resolving_it()
    {
        using (var db = authDb.CreateDbContext())
        {
            db.Households.Add(new Household { Id = "hh-a", Name = "The Currans" });
            db.SaveChanges();
        }
        SeedReport("hh-a", "The chart looks wrong");
        var cut = Render<Components.Pages.Admin>();
        cut.WaitForAssertion(() => Assert.Contains("Propose resolved", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Propose resolved").Click();

        // Still OPEN (not resolved), now awaiting the reporter — the unilateral override stays available.
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("awaiting the reporter", cut.Markup);
            Assert.DoesNotContain("Resolved (1)", cut.Markup);
            Assert.Contains("Withdraw", cut.FindAll("button").Select(b => b.TextContent.Trim()));
        });
        await cut.WaitForAssertionAsync(async () =>
        {
            await using var raw = Db.CreateUnscopedContext();
            var report = await raw.BugReports.IgnoreQueryFilters().SingleAsync();
            Assert.NotNull(report.ProposedResolvedAt);
            Assert.Null(report.ResolvedAt);
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

    [Fact]
    public async Task The_open_error_log_sorts_by_count()
    {
        var store = new ErrorLogStore(authDb);
        async Task Record(string msg, int times)
        {
            for (var i = 0; i < times; i++)
                await store.RecordAsync(new CapturedError(DateTimeOffset.Now, "Error", "Cat", "Type", msg, msg, null));
        }
        await Record("AlphaErr", 1);
        await Record("BravoErr", 3);
        await Record("CharlieErr", 2);

        var cut = Render<Components.Pages.Admin>();
        cut.WaitForAssertion(() => Assert.Contains("AlphaErr", cut.Markup));

        cut.FindAll("button.th-sort").First(b => b.TextContent.Contains("Count")).Click(); // ascending by count

        var m = cut.Markup;
        Assert.True(m.IndexOf("AlphaErr") < m.IndexOf("CharlieErr") && m.IndexOf("CharlieErr") < m.IndexOf("BravoErr"),
            "Ascending by count should order 1×, 2×, 3× (Alpha, Charlie, Bravo).");
    }

    // ------------------------------------------------------------------- demo box valve

    [Fact]
    public void The_demo_usage_panel_shows_todays_calls_against_the_cap_when_configured()
    {
        demoOptions.DailyGlobalCallLimit = 300;
        using (var db = authDb.CreateDbContext())
        {
            db.DemoUsage.Add(new DemoUsageDay { Day = DateOnly.FromDateTime(DateTime.Today), Calls = 42 });
            db.SaveChanges();
        }

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Demo box usage", cut.Markup);
            Assert.Contains("42 / 300", cut.Markup); // today's box-wide calls against the cap
        });
    }

    [Fact]
    public void The_demo_usage_panel_is_hidden_on_a_box_with_no_demo_config()
    {
        // demoOptions is left unconfigured (the family / self-host default) — the valve is a no-op, so the
        // panel must not appear at all (removing the IsConfigured gate would render "0" on every box).
        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("At a glance", cut.Markup);            // the page rendered
            Assert.DoesNotContain("Demo box usage", cut.Markup);  // …but not the demo panel
        });
    }

    [Fact]
    public void The_demo_usage_panel_flags_the_cap_being_reached()
    {
        demoOptions.DailyGlobalCallLimit = 5;
        using (var db = authDb.CreateDbContext())
        {
            db.DemoUsage.Add(new DemoUsageDay { Day = DateOnly.FromDateTime(DateTime.Today), Calls = 5 });
            db.SaveChanges();
        }

        var cut = Render<Components.Pages.Admin>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("5 / 5", cut.Markup);
            Assert.Contains("capped", cut.Markup); // the "· capped" label the at-cap styling carries
        });
    }
}
