using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>The reporter's own resolve/reopen — the household-SCOPED half of the loop (the admin's
/// cross-household half is <see cref="ReportResolutionServiceTests"/>). What these pin: a reporter can
/// settle a report their OWN household filed, and CANNOT touch another household's — the query filter,
/// which is exactly what the admin path deliberately drops with IgnoreQueryFilters and this one keeps.</summary>
public class ReporterReportServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private ReporterReportService Service() => new(_db);

    private int SeedReport(string household)
    {
        _db.HouseholdId = household;
        using var db = _db.CreateDbContext();
        var report = new BugReport { Body = "looks wrong", CreatedAt = DateTimeOffset.Now.AddDays(-1) };
        db.BugReports.Add(report);
        db.SaveChanges();
        return report.Id;
    }

    private async Task<BugReport> ReadAsync(int id)
    {
        await using var raw = _db.CreateUnscopedContext();
        return await raw.BugReports.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == id);
    }

    [Fact]
    public async Task Resolving_your_own_report_stamps_it()
    {
        var id = SeedReport("hh-a");
        _db.HouseholdId = "hh-a"; // the reporter is IN hh-a

        Assert.True(await Service().ResolveOwnAsync(id));

        Assert.NotNull((await ReadAsync(id)).ResolvedAt);
    }

    [Fact]
    public async Task Reopening_your_own_report_clears_both_stamps()
    {
        var id = SeedReport("hh-a");
        _db.HouseholdId = "hh-a";
        // A proposed-and-resolved starting state, so the reopen has both stamps to clear.
        await using (var db = _db.CreateDbContext())
        {
            await db.BugReports.Where(b => b.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ProposedResolvedAt, DateTimeOffset.Now)
                .SetProperty(b => b.ResolvedAt, DateTimeOffset.Now));
        }

        Assert.True(await Service().ReopenOwnAsync(id));

        var report = await ReadAsync(id);
        Assert.Null(report.ResolvedAt);
        Assert.Null(report.ProposedResolvedAt); // never lingers as "awaiting reporter"
    }

    [Fact]
    public async Task A_reporter_cannot_touch_another_households_report()
    {
        // ⚠️ THE tenancy test. hh-a filed it; the caller is scoped to hh-b. The query filter (which the
        // admin path deliberately DROPS with IgnoreQueryFilters, and this one deliberately KEEPS) scopes
        // the WHERE to hh-b, so hh-a's report is invisible: the write matches 0 rows and changes nothing.
        // Adding IgnoreQueryFilters to the service must fail this test.
        var id = SeedReport("hh-a");
        _db.HouseholdId = "hh-b"; // a DIFFERENT household

        Assert.False(await Service().ResolveOwnAsync(id));
        Assert.False(await Service().ReopenOwnAsync(id));

        Assert.Null((await ReadAsync(id)).ResolvedAt); // hh-a's report untouched
    }

    [Fact]
    public async Task A_missing_report_answers_false()
    {
        // Deleted with its household's data between the render and the click, say — not a throw.
        _db.HouseholdId = "hh-a";
        Assert.False(await Service().ResolveOwnAsync(9999));
        Assert.False(await Service().ReopenOwnAsync(9999));
    }
}
