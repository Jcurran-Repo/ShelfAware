using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
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
        new ErrorLogStore(_authDb));

    private void SeedReport(string household, string body)
    {
        _db.HouseholdId = household;
        using var db = _db.CreateDbContext();
        db.BugReports.Add(new BugReport { Body = body, CreatedAt = DateTimeOffset.Now });
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
    public async Task Anyone_but_the_configured_admin_is_refused_by_both_lists()
    {
        SeedReport("hh-a", "Private to hh-a");
        var reader = Reader(signedInAs: "wife@example.com");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ListBugReportsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ListErrorsAsync());
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
