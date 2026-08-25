using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;
using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.Tests;

/// <summary>The one cross-household WRITE in the app, and the gate on it. The write is
/// column-scoped by construction (the ExecuteUpdate names ResolvedAt and nothing else), so what
/// these pin is the gate, the REACH (any household's report, which the query filter would
/// otherwise silently deny), and that reach's LIMIT (nothing but the stamp changes). The error
/// half also pins the derived resolution rule: a recurrence reopens a row with zero involvement
/// from the capture pipeline.</summary>
public class ReportResolutionServiceTests : IDisposable
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

    private ReportResolutionService Service(string signedInAs = AdminEmail) => new(
        _db,
        new FakeAuthState(SignedIn(signedInAs)),
        Options.Create(new AdminOptions { Emails = [AdminEmail] }),
        new ErrorLogStore(_authDb));

    private int SeedReport(string household, string body)
    {
        _db.HouseholdId = household;
        using var db = _db.CreateDbContext();
        var report = new BugReport
        {
            Body = body,
            PageUrl = "/list",
            ReportedBy = "wife@example.com",
            CreatedAt = DateTimeOffset.Now.AddDays(-1),
        };
        db.BugReports.Add(report);
        db.SaveChanges();
        return report.Id;
    }

    private static CapturedError Error(DateTimeOffset at, string template = "Loading failed") => new(
        at, "Error", "ShelfAware.Web.Components.Pages.Home",
        "System.InvalidOperationException", template, template, "stack detail");

    [Fact]
    public async Task Resolving_stamps_a_foreign_households_report_and_changes_nothing_else()
    {
        var id = SeedReport("hh-a", "The chart looks wrong");
        BugReport before;
        await using (var pre = _db.CreateUnscopedContext())
        {
            before = await pre.BugReports.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        }
        // ⚠️ The admin acts from their OWN household's scope — the whole point of the
        // IgnoreQueryFilters is that the filter would otherwise pin the WHERE to this id and
        // silently miss hh-a's row. Dropping it must fail this test, not merely narrow it.
        _db.HouseholdId = "hh-admin";

        Assert.True(await Service().SetBugResolvedAsync(id, DateTimeOffset.Now));

        await using var raw = _db.CreateUnscopedContext();
        var report = Assert.Single(await raw.BugReports.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        Assert.NotNull(report.ResolvedAt);
        // The reach's limit, pinned by REFLECTION rather than a named-field list: a property added
        // to BugReport later is automatically covered, because the claim is "the stamp is the ONLY
        // thing an admin can change on household data" — not "these four fields survived".
        foreach (var p in typeof(BugReport).GetProperties()
                     .Where(p => p.Name is not nameof(BugReport.ResolvedAt) and not nameof(BugReport.Resolved)))
        {
            Assert.Equal(p.GetValue(before), p.GetValue(report));
        }
    }

    [Fact]
    public async Task Reopening_clears_the_stamp()
    {
        var id = SeedReport("hh-a", "Still broken actually");
        _db.HouseholdId = "hh-admin";
        await Service().SetBugResolvedAsync(id, DateTimeOffset.Now);

        Assert.True(await Service().SetBugResolvedAsync(id, null));

        await using var raw = _db.CreateUnscopedContext();
        Assert.Null((await raw.BugReports.IgnoreQueryFilters().SingleAsync()).ResolvedAt);
    }

    [Fact]
    public async Task Proposing_stamps_a_foreign_households_report_as_awaiting_the_reporter()
    {
        var id = SeedReport("hh-a", "The chart looks wrong");
        _db.HouseholdId = "hh-admin"; // the admin's own scope — IgnoreQueryFilters is what reaches hh-a

        Assert.True(await Service().SetBugProposedAsync(id, DateTimeOffset.Now));

        await using var raw = _db.CreateUnscopedContext();
        var report = await raw.BugReports.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.NotNull(report.ProposedResolvedAt);
        Assert.True(report.AwaitingReporter); // proposed + not yet resolved
        Assert.Null(report.ResolvedAt);       // a proposal is not a resolve
    }

    [Fact]
    public async Task Reopening_also_clears_a_pending_proposal()
    {
        // ⚠️ Reopen (or Withdraw) returns the report to fully OPEN — a lingering proposal would leave it
        // reading "awaiting reporter" forever. Dropping the second SetProperty must fail this test.
        var id = SeedReport("hh-a", "Proposed then withdrawn");
        _db.HouseholdId = "hh-admin";
        await Service().SetBugProposedAsync(id, DateTimeOffset.Now);

        Assert.True(await Service().SetBugResolvedAsync(id, null));

        await using var raw = _db.CreateUnscopedContext();
        var report = await raw.BugReports.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Null(report.ProposedResolvedAt);
        Assert.Null(report.ResolvedAt);
        Assert.False(report.AwaitingReporter);
    }

    [Fact]
    public async Task A_report_that_no_longer_exists_answers_false_rather_than_throwing()
    {
        // Deleted with its household's data between the render and the click, say.
        Assert.False(await Service().SetBugResolvedAsync(9999, DateTimeOffset.Now));
        Assert.False(await Service().SetBugProposedAsync(9999, DateTimeOffset.Now));
    }

    [Fact]
    public async Task Anyone_but_the_configured_admin_is_refused_by_every_admin_write()
    {
        var id = SeedReport("hh-a", "Private to hh-a");
        var store = new ErrorLogStore(_authDb);
        await store.RecordAsync(Error(DateTimeOffset.Now));
        // The RECORDED row's id, not a literal: a hard-coded 1 kept passing whether the gate
        // refused the call or the call simply matched no row, so the data half of this test
        // proved nothing about the error store.
        var errorId = Assert.Single(await store.ListAsync()).Id;
        var intruder = Service(signedInAs: "wife@example.com");

        // EVERY cross-household write is gated — including the newest, SetBugProposedAsync (the class
        // the repo elevates above all others; a gate that no test exercises is a gate a refactor drops).
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            intruder.SetBugResolvedAsync(id, DateTimeOffset.Now));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            intruder.SetBugProposedAsync(id, DateTimeOffset.Now));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            intruder.SetErrorResolvedAsync(errorId, DateTimeOffset.Now));

        // Refused BEFORE any data was touched, not after.
        await using var raw = _db.CreateUnscopedContext();
        var report = await raw.BugReports.IgnoreQueryFilters().SingleAsync();
        Assert.Null(report.ResolvedAt);
        Assert.Null(report.ProposedResolvedAt);
        Assert.Null(Assert.Single(await store.ListAsync()).ResolvedAt);
    }

    [Fact]
    public async Task An_occurrence_after_the_seen_stamp_reopens_even_when_it_predates_the_click()
    {
        // The resolve stamps what the admin SAW (the rendered row's LastSeenAt), never the click's
        // clock — so a recurrence from the render-to-click window, or one captured earlier but
        // persisted later by the background writer, lands past the mark and the row comes back.
        // A now-stamp would swallow both silently.
        var store = new ErrorLogStore(_authDb);
        var seen = DateTimeOffset.Now.AddHours(-2);
        await store.RecordAsync(Error(seen));
        var id = Assert.Single(await store.ListAsync()).Id;
        await store.RecordAsync(Error(seen.AddMinutes(30))); // fires after the admin's render…

        Assert.True(await Service().SetErrorResolvedAsync(id, seen)); // …who resolves what they SAW

        Assert.False(Assert.Single(await store.ListAsync()).Resolved); // the unseen recurrence survives
    }

    [Fact]
    public async Task A_resolved_error_reads_resolved_until_it_recurs()
    {
        // The derived rule, end to end: resolution is judged by comparing stamps, so RecordAsync —
        // completely unaware the field exists — reopens the row just by bumping LastSeenAt. A
        // recurring error can never sit hidden behind a stale resolve.
        var store = new ErrorLogStore(_authDb);
        var firstSeen = DateTimeOffset.Now.AddHours(-2);
        await store.RecordAsync(Error(firstSeen));
        var id = Assert.Single(await store.ListAsync()).Id;

        Assert.True(await Service().SetErrorResolvedAsync(id, DateTimeOffset.Now.AddHours(-1)));
        Assert.True(Assert.Single(await store.ListAsync()).Resolved);

        await store.RecordAsync(Error(DateTimeOffset.Now)); // same fingerprint, later
        var row = Assert.Single(await store.ListAsync());
        Assert.False(row.Resolved);          // active again, by derivation alone
        Assert.NotNull(row.ResolvedAt);      // the history of having been resolved once survives
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public async Task Reopening_an_error_clears_the_stamp()
    {
        // The admin's Reopen (a null stamp), not the derived recurrence reopen: the stamp itself
        // is cleared, so the row reads open with no recurred-after-resolve note — Reopen means
        // "not actually handled", not "handled and back".
        var store = new ErrorLogStore(_authDb);
        await store.RecordAsync(Error(DateTimeOffset.Now.AddHours(-2)));
        var id = Assert.Single(await store.ListAsync()).Id;
        await Service().SetErrorResolvedAsync(id, DateTimeOffset.Now.AddHours(-1));
        Assert.True(Assert.Single(await store.ListAsync()).Resolved); // really resolved first

        Assert.True(await Service().SetErrorResolvedAsync(id, null));

        var row = Assert.Single(await store.ListAsync());
        Assert.False(row.Resolved);
        Assert.Null(row.ResolvedAt);
    }

    [Fact]
    public async Task An_error_row_the_trim_already_took_answers_false()
    {
        Assert.False(await Service().SetErrorResolvedAsync(9999, DateTimeOffset.Now));
    }

    public static TheoryData<string?, string, bool> ResolvedCases => new()
    {
        // (resolvedAt, lastSeenAt, expected) — ISO strings so the theory serializes cleanly.
        { null, "2026-08-14T10:00:00+00:00", false },                          // never resolved
        { "2026-08-14T10:00:00+00:00", "2026-08-14T09:00:00+00:00", true },    // quiet since
        { "2026-08-14T10:00:00+00:00", "2026-08-14T10:00:00+00:00", true },    // stamped at the same instant
        { "2026-08-14T10:00:00+00:00", "2026-08-14T11:00:00+00:00", false },   // recurred after
    };

    [Theory]
    [MemberData(nameof(ResolvedCases))]
    public void Resolution_is_derived_from_the_two_stamps(string? resolvedAt, string lastSeenAt, bool expected)
    {
        var row = new ErrorLogEntry
        {
            Fingerprint = "f", Level = "Error", Category = "c", LastMessage = "m",
            ResolvedAt = resolvedAt is null ? null : DateTimeOffset.Parse(resolvedAt),
            LastSeenAt = DateTimeOffset.Parse(lastSeenAt),
        };

        Assert.Equal(expected, row.Resolved);
    }
}
