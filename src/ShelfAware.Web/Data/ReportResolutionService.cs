using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.Data;

/// <summary>The admin's "mark it handled" writes — and the ONE place in the app that writes across
/// households, kept deliberately as narrow as a write can be.
/// <para>The bug half sets exactly <see cref="ShelfAware.Core.Domain.BugReport.ResolvedAt"/> by
/// report id through a column-scoped ExecuteUpdate: no tracked entity and no SaveChanges, so
/// nothing else CAN ride along — and the tenancy guard (ShelfAwareDbContext.EnforceHousehold)
/// still refuses every tracked cross-household write in the app, because this path never enters
/// the change tracker it polices. The error half is not a tenancy matter at all (ErrorLog is
/// operator data in auth.db) and delegates to the store that owns the table.</para>
/// <para>Every method re-verifies the caller against <see cref="AdminOptions.IsAdmin"/> — the same
/// defense-in-depth layer <see cref="AdminReportReader"/> carries, and the layer a
/// directly-rendered component test can actually pin. The reader stays read-only by design; this
/// class exists so that claim never has to soften.</para></summary>
public sealed class ReportResolutionService(
    IHouseholdDbFactory dbFactory,
    AuthenticationStateProvider auth,
    IOptions<AdminOptions> admin,
    ErrorLogStore errors)
{
    /// <summary>Stamp (or, with null, reopen) a bug report, whichever household filed it. Returns
    /// false when no such report exists any more (deleted with its household's data, say).
    /// ⚠️ No CancellationToken parameter, on purpose — item 38's write rule: a resolve is a
    /// one-shot write, and a caller threading a page token would let a navigate-away tear the
    /// stamp down mid-flight with no message and no retry surface. The signature is the pin.</summary>
    public async Task<bool> SetBugResolvedAsync(int id, DateTimeOffset? resolvedAt)
    {
        await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        // ⚠️ The app's one production cross-household WRITE, the mirror of the reader's one
        // IgnoreQueryFilters read: without it the query filter would scope the WHERE to the
        // admin's own household and every other household's report would answer "gone". The
        // ExecuteUpdate is the point, not a convenience — it can only ever touch the column
        // named on this line. Anything else wanting to write across households makes its own
        // case at review; don't widen this.
        return await db.BugReports.IgnoreQueryFilters()
            .Where(b => b.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.ResolvedAt, resolvedAt), CancellationToken.None) > 0;
    }

    /// <summary>Stamp (or, with null, reopen) an error-log row. Operator data — the gate here is
    /// about WHO may act, not whose data it is. ⚠️ For a RESOLVE, pass the LastSeenAt the admin
    /// was looking at, never the clock: resolution means "handled through what I saw", and a
    /// now-stamp silently swallows occurrences from the render-to-click window (see
    /// <see cref="ErrorLogEntry.ResolvedAt"/>). Uncancellable for the same reason as the bug half.</summary>
    public async Task<bool> SetErrorResolvedAsync(int id, DateTimeOffset? resolvedAt)
    {
        await RequireAdminAsync();
        return await errors.SetResolvedAsync(id, resolvedAt);
    }

    private async Task RequireAdminAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        if (!admin.Value.IsAdmin(state.User))
            throw new UnauthorizedAccessException("Resolving reports is only for the configured admin.");
    }
}
