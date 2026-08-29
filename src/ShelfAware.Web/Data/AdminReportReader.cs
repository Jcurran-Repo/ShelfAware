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

/// <summary>One audit-trail row for the admin's "Recent activity" view — the household's own /history
/// line, plus the household name, projected read-only (the operator only ever LOOKS at it). This is
/// the "link" between the activity log and the error log: an operator seeing an error can scan what
/// households were doing around that time.</summary>
public sealed record AdminActivityRow(DateTimeOffset When, string HouseholdName, string Summary, string? Source, bool Undone);

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

    /// <summary>The "Recent activity" panel shows at most this many audit-trail rows — bounded like the
    /// error log's MaxRows, so a busy deployment can't degrade the surface. Older activity lives on each
    /// household's own History page.</summary>
    public const int MaxActivity = 100;

    public async Task<IReadOnlyList<AdminBugReport>> ListBugReportsAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // ⚠️ One of the app's FOUR production IgnoreQueryFilters — its write mirror is
        // ReportResolutionService, AdminAiSpendReader's cross-household usage aggregate is another, and
        // ListRecentActivityAsync (below) reads the audit trail (each admin-gated + AsNoTracking; their
        // docs carry the shape and the same warning). Deliberate and narrow: bug reports are addressed TO
        // the admin, this service is their only reader, the read is AsNoTracking so no write can ride on
        // it, and RequireAdminAsync just refused everyone else. Anything else wanting cross-household data
        // must make its own case at review — not reuse any of these.
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

    /// <summary>The recent audit trail across every household — the "link" that puts /history's actions
    /// beside the error log, so an operator can see what people were doing around the time an error fired.
    /// Read-only and admin-gated; the operator never edits another household's activity.</summary>
    public async Task<IReadOnlyList<AdminActivityRow>> ListRecentActivityAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // ⚠️ The FOURTH production IgnoreQueryFilters (see the warning on ListBugReportsAsync): the audit
        // trail read. AsNoTracking + a projection, so no entity — and no write — can ride out of it, and
        // RequireAdminAsync just refused everyone else. Ordered by Id DESCENDING: insert order IS
        // chronological (item 51), and SQLite refuses ORDER BY on the DateTimeOffset OccurredAt column.
        var rows = await db.ActivityEntries.IgnoreQueryFilters().AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Take(MaxActivity)
            .Select(e => new { e.OccurredAt, e.HouseholdId, e.Summary, e.Source, e.UndoneAt })
            .ToListAsync(ct);

        await using var authContext = await authDb.CreateDbContextAsync(ct);
        var names = await authContext.Households.AsNoTracking()
            .ToDictionaryAsync(h => h.Id, h => h.Name, ct);

        return rows
            .Select(r => new AdminActivityRow(
                r.OccurredAt,
                r.HouseholdId is { } hh && names.TryGetValue(hh, out var name) ? name : "(household gone)",
                r.Summary,
                r.Source,
                r.UndoneAt is not null)) // the household reversed it — the correlation view must say so, not show it as a live action
            .ToList();
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
