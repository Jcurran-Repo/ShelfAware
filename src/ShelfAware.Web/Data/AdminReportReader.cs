using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.Data;

/// <summary>A bug report joined with the name of the household that filed it (households live in
/// auth.db, so there is no navigation to include).</summary>
public sealed record AdminBugReport(BugReport Report, string HouseholdName);

/// <summary>The admin page's data, and the ONE place in the app that reads across households.
/// Every list re-verifies the caller against <see cref="AdminOptions.IsAdmin"/> before touching
/// data — the routed page already carries the Admin policy, so this is defense in depth, and it is
/// the layer a component test can actually pin (a directly-rendered component bypasses routing
/// authorization). Read-only by design: the admin sees every household's reports, never writes
/// them.</summary>
public sealed class AdminReportReader(
    IHouseholdDbFactory dbFactory,
    IDbContextFactory<AuthDbContext> authDb,
    AuthenticationStateProvider auth,
    IOptions<AdminOptions> admin,
    ErrorLogStore errors)
{
    /// <summary>The admin page loads at most this many reports (open ones first, newest within
    /// each half — ListBugReportsAsync's ordering) — the same bounded posture as the error log's
    /// MaxRows, so one prolific account can't degrade the surface. The page discloses when the
    /// cap is hit rather than truncating silently.</summary>
    public const int MaxReports = 500;

    public async Task<IReadOnlyList<AdminBugReport>> ListBugReportsAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // ⚠️ One of exactly TWO production IgnoreQueryFilters — the write mirror is
        // ReportResolutionService (its doc carries the shape and the same warning). Deliberate and
        // narrow: bug reports are addressed TO the admin, this service is their only reader, the
        // read is AsNoTracking so no write can ride on it, and RequireAdminAsync just refused
        // everyone else. Anything else wanting cross-household data must make its own case at
        // review — not reuse either of these.
        var reports = await db.BugReports.IgnoreQueryFilters().AsNoTracking()
            // Open FIRST, then newest: the cap must never be spent on resolved rows while open
            // ones — the to-do list this surface exists for — fall off the far end. A null check
            // translates fine; it's ORDER BY a DateTimeOffset that SQLite refuses.
            .OrderBy(r => r.ResolvedAt != null)
            .ThenByDescending(r => r.Id) // insert order IS chronological within each half
            .Take(MaxReports)
            .ToListAsync(ct);

        await using var authContext = await authDb.CreateDbContextAsync(ct);
        var names = await authContext.Households.AsNoTracking()
            .ToDictionaryAsync(h => h.Id, h => h.Name, ct);

        return reports
            .Select(r => new AdminBugReport(r,
                r.HouseholdId is { } hh && names.TryGetValue(hh, out var name) ? name : "(household gone)"))
            .ToList();
    }

    public async Task<IReadOnlyList<ErrorLogEntry>> ListErrorsAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync();
        return await errors.ListAsync(ct);
    }

    private async Task RequireAdminAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        if (!admin.Value.IsAdmin(state.User))
            throw new UnauthorizedAccessException("The admin view is only for the configured admin.");
    }
}
