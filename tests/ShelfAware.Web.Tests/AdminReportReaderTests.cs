using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;
using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.Tests;

/// <summary>The one cross-household read in the app. The gate is the load-bearing part: every list
/// re-verifies the caller against AdminOptions before touching data, whoever managed to call it —
/// the routed page's policy is the front door, this is the lock on the room.</summary>
public class AdminReportReaderTests : IDisposable
{
    private const string AdminEmail = "jordan@example.com";

    private readonly TestDb _db = new();
    private readonly TestAuthDb _authDb = new();

    public void Dispose()
    {
        _db.Dispose();
        _authDb.Dispose();
    }

    private sealed class FakeAuthState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private static ClaimsPrincipal SignedIn(string email) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, email)], authenticationType: "test"));

    private AdminReportReader Reader(string signedInAs = AdminEmail) => new(
        _db,
        _authDb,
        new FakeAuthState(SignedIn(signedInAs)),
        Options.Create(new AdminOptions { Emails = [AdminEmail] }),
        new ErrorLogStore(_authDb),
        new LoginAudit(_authDb, NullLogger<LoginAudit>.Instance));

    private void SeedReport(string household, string body)
    {
        _db.HouseholdId = household;
        using var db = _db.CreateDbContext();
        db.BugReports.Add(new BugReport { Body = body, CreatedAt = DateTimeOffset.Now });
        db.SaveChanges();
    }

    private void SeedActivity(string household, string summary, string? source = null)
    {
        _db.HouseholdId = household; // stamped onto the entry on insert, like any household-owned row
        using var db = _db.CreateDbContext();
        db.ActivityEntries.Add(new ActivityEntry
        {
            Summary = summary,
            Source = source,
            OccurredAt = DateTimeOffset.Now,
            Kind = ActivityKind.SignalRecorded,
            Reversibility = Reversibility.Reversible,
            PayloadJson = "{}",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task The_admin_sees_every_households_reports_with_their_names()
    {
        using (var auth = _authDb.CreateDbContext())
        {
            auth.Households.Add(new Household { Id = "hh-a", Name = "The Currans" });
            auth.Households.Add(new Household { Id = "hh-b", Name = "The Neighbours" });
            auth.SaveChanges();
        }
        SeedReport("hh-a", "The dashboard chart looks wrong");
        SeedReport("hh-b", "The list printed sideways");
        SeedReport("hh-c", "From a household that was since deleted");

        var reports = await Reader().ListBugReportsAsync();

        Assert.Equal(3, reports.Count);
        Assert.Contains(reports, r => r.Report.Body.Contains("chart") && r.HouseholdName == "The Currans");
        Assert.Contains(reports, r => r.Report.Body.Contains("sideways") && r.HouseholdName == "The Neighbours");
        Assert.Contains(reports, r => r.Report.Body.Contains("deleted") && r.HouseholdName == "(household gone)");
    }

    [Fact]
    public async Task Anyone_but_the_configured_admin_is_refused_by_every_list()
    {
        SeedReport("hh-a", "Private to hh-a");
        var reader = Reader(signedInAs: "wife@example.com");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ListBugReportsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ListErrorsAsync());
        // The login history is a who's-been-here list — a non-admin must never read it.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ListLoginStatsAsync());
        // The audit trail across households is the sharpest cross-household read — gated like the rest.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ListRecentActivityAsync());
    }

    [Fact]
    public async Task The_admin_reads_the_login_stats_through_the_same_gate()
    {
        var audit = new LoginAudit(_authDb, NullLogger<LoginAudit>.Instance);
        await audit.RecordAsync("u1", "jordan@example.com", DateTimeOffset.Now);
        await audit.RecordAsync("u1", "jordan@example.com", DateTimeOffset.Now.AddHours(1));

        var stats = await Reader().ListLoginStatsAsync();

        var row = Assert.Single(stats);
        Assert.Equal("jordan@example.com", row.Email);
        Assert.Equal(2, row.LoginCount);
    }

    [Fact]
    public async Task The_report_list_is_bounded_at_the_newest_MaxReports()
    {
        // The same bounded posture as the error log's MaxRows: one prolific account must not be
        // able to degrade the admin surface. The page discloses the cap; the reader enforces it.
        _db.HouseholdId = "hh-a";
        using (var db = _db.CreateDbContext())
        {
            for (var i = 0; i < AdminReportReader.MaxReports + 5; i++)
            {
                db.BugReports.Add(new BugReport { Body = $"report {i}", CreatedAt = DateTimeOffset.Now });
            }
            db.SaveChanges();
        }

        var reports = await Reader().ListBugReportsAsync();

        Assert.Equal(AdminReportReader.MaxReports, reports.Count);
        Assert.Equal($"report {AdminReportReader.MaxReports + 4}", reports[0].Report.Body); // newest survived the cap
    }

    [Fact]
    public async Task The_admin_sees_recent_activity_across_households_newest_first_with_names()
    {
        using (var auth = _authDb.CreateDbContext())
        {
            auth.Households.Add(new Household { Id = "hh-a", Name = "The Currans" });
            auth.Households.Add(new Household { Id = "hh-b", Name = "The Neighbours" });
            auth.SaveChanges();
        }
        SeedActivity("hh-a", "Bought Milk", "Manual");
        SeedActivity("hh-b", "Marked Eggs out", "Chat");
        SeedActivity("hh-c", "From a since-deleted household");

        var rows = await Reader().ListRecentActivityAsync();

        // Every household's activity, newest first (by Id — insert order is chronological), each named.
        Assert.Equal(3, rows.Count);
        Assert.Equal("From a since-deleted household", rows[0].Summary);
        Assert.Equal("(household gone)", rows[0].HouseholdName);
        Assert.Contains(rows, r => r.Summary == "Bought Milk" && r.HouseholdName == "The Currans" && r.Source == "Manual");
        Assert.Contains(rows, r => r.Summary == "Marked Eggs out" && r.HouseholdName == "The Neighbours");
    }

    [Fact]
    public async Task The_activity_list_is_bounded_at_the_newest_MaxActivity()
    {
        _db.HouseholdId = "hh-a";
        using (var db = _db.CreateDbContext())
        {
            for (var i = 0; i < AdminReportReader.MaxActivity + 5; i++)
            {
                db.ActivityEntries.Add(new ActivityEntry
                {
                    Summary = $"action {i}", OccurredAt = DateTimeOffset.Now, Kind = ActivityKind.SignalRecorded,
                    Reversibility = Reversibility.Reversible, PayloadJson = "{}",
                });
            }
            db.SaveChanges();
        }

        var rows = await Reader().ListRecentActivityAsync();

        Assert.Equal(AdminReportReader.MaxActivity, rows.Count);
        Assert.Equal($"action {AdminReportReader.MaxActivity + 4}", rows[0].Summary); // newest survived the cap
    }

    [Fact]
    public async Task An_undone_action_is_flagged_so_the_operator_does_not_read_a_reversed_action_as_live()
    {
        _db.HouseholdId = "hh-a";
        using (var db = _db.CreateDbContext())
        {
            db.ActivityEntries.Add(new ActivityEntry
            {
                Summary = "Bought Milk", OccurredAt = DateTimeOffset.Now, Kind = ActivityKind.SignalRecorded,
                Reversibility = Reversibility.Reversible, PayloadJson = "{}", UndoneAt = DateTimeOffset.Now, // reversed
            });
            db.ActivityEntries.Add(new ActivityEntry
            {
                Summary = "Marked Eggs out", OccurredAt = DateTimeOffset.Now, Kind = ActivityKind.SignalRecorded,
                Reversibility = Reversibility.Reversible, PayloadJson = "{}", // still stands
            });
            db.SaveChanges();
        }

        var rows = await Reader().ListRecentActivityAsync();

        Assert.True(rows.Single(r => r.Summary == "Bought Milk").Undone);
        Assert.False(rows.Single(r => r.Summary == "Marked Eggs out").Undone);
    }

    [Fact]
    public async Task The_admin_reads_the_error_log_through_the_same_gate()
    {
        var store = new ErrorLogStore(_authDb);
        await store.RecordAsync(new CapturedError(
            DateTimeOffset.Now, "Error", "ShelfAware.Web.Components.Pages.Home",
            "System.InvalidOperationException", "It broke", "It broke", "detail"));

        var errors = await Reader().ListErrorsAsync();

        var row = Assert.Single(errors);
        Assert.Equal("It broke", row.LastMessage);
    }
}
