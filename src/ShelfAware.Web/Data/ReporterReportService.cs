using Microsoft.EntityFrameworkCore;

namespace ShelfAware.Web.Data;

/// <summary>The REPORTER's own resolve/reopen of a bug report THEY filed — the other half of the loop
/// from <see cref="ReportResolutionService"/> (the ADMIN's, cross-household). This one is deliberately
/// household-SCOPED: it uses the ordinary <see cref="IHouseholdDbFactory"/> context, so the global query
/// filter scopes every write's WHERE to the caller's own household. A reporter can confirm the admin's
/// proposal, self-resolve a report they've found fixed, or reopen one — but the filter makes it
/// structurally impossible to touch another household's report. The difference from the admin path is
/// exactly its <c>IgnoreQueryFilters</c>: this one has none, so it never reaches across. No admin gate —
/// a reporter acting on their own household's data needs none.
///
/// ⚠️ No CancellationToken, matching <see cref="ReportResolutionService"/>: these are one-shot writes,
/// and a page token threaded here would let a navigate-away tear a resolve down mid-flight with no
/// retry surface (item 38's write rule).</summary>
public sealed class ReporterReportService(IHouseholdDbFactory dbFactory)
{
    /// <summary>Mark the reporter's own report resolved — confirming the admin's proposal, or
    /// self-resolving one they've found fixed. Returns false if no such report exists in this
    /// household (a foreign id, or one deleted with its household's data).</summary>
    public async Task<bool> ResolveOwnAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        // NOT IgnoreQueryFilters (unlike the admin path): the household filter scopes the WHERE, so this
        // can only ever resolve a report the caller's OWN household filed.
        return await db.BugReports
            .Where(b => b.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.ResolvedAt, DateTimeOffset.Now), CancellationToken.None) > 0;
    }

    /// <summary>Reopen the reporter's own report to fully OPEN — rejecting a proposal ("still broken") or
    /// reopening a resolved one. Clears both stamps, so it never lingers as "awaiting reporter".</summary>
    public async Task<bool> ReopenOwnAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        return await db.BugReports
            .Where(b => b.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ResolvedAt, (DateTimeOffset?)null)
                .SetProperty(b => b.ProposedResolvedAt, (DateTimeOffset?)null), CancellationToken.None) > 0;
    }
}
