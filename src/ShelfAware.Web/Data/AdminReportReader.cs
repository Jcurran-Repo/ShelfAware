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
    ErrorLogStore errors,
    LoginAudit logins)
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

        // ⚠️ One of the app's THREE production IgnoreQueryFilters — its write mirror is
        // ReportResolutionService, and AdminAiSpendReader's cross-household usage aggregate is the third
        // (each admin-gated + AsNoTracking; their docs carry the shape and the same warning). Deliberate
        // and narrow: bug reports are addressed TO the admin, this service is their only reader, the
        // read is AsNoTracking so no write can ride on it, and RequireAdminAsync just refused
        // everyone else. Anything else wanting cross-household data must make its own case at
        // review — not reuse any of these.
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

    /// <summary>Per-account login stats (auth.db operator data), most-recently-active first — the
    /// persisted half of the "who's logged in" view. Admin-gated like the rest, so a login history is
    /// never served to anyone but the admin.</summary>
    public async Task<IReadOnlyList<UserLoginStat>> ListLoginStatsAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync();
        return await logins.ListAsync(ct);
    }

    private async Task RequireAdminAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        if (!admin.Value.IsAdmin(state.User))
            throw new UnauthorizedAccessException("The admin view is only for the configured admin.");
    }
}
