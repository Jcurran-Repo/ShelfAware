using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Data;

/// <summary>
/// The operator's cross-household AI spend — today and this calendar month, summed across every household
/// on the deployment (the admin dashboard's "at a glance" AI figures). This is the ONLY thing on the admin
/// surface that reads a pantry table across households, and it is admin-gated exactly as
/// <see cref="AdminReportReader"/> is.
///
/// ⚠️ This carries one of the app's FOUR production IgnoreQueryFilters — the others are AdminReportReader's
/// bug-report read, its ListRecentActivityAsync audit-trail read, and the ReportResolutionService write
/// mirror. It is a DELIBERATE new case, made at
/// review, NOT a reuse of those: the operator legitimately needs the total AI cost across households
/// (<see cref="Core.Domain.AiUsage.CostMicros"/> is recorded in every key mode precisely so this number can
/// exist), and no per-household surface answers it. It is safe the same way the report reader is —
/// <see cref="RequireAdminAsync"/> refuses everyone else BEFORE any data is touched; the read is
/// AsNoTracking, so no write can ride on it; it only ever AGGREGATES (calls/tokens/cost) and never returns a
/// household's own rows; and it bounds itself to the current month. Anything else wanting cross-household
/// pantry data makes its own case at review — don't reuse this either.
/// </summary>
public sealed class AdminAiSpendReader(
    IHouseholdDbFactory dbFactory,
    AuthenticationStateProvider auth,
    IOptions<AdminOptions> admin)
{
    public async Task<AiSpendReport> GetAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        // ⚠️ One of the app's four production IgnoreQueryFilters — see the class doc. dbFactory pre-set the context to the
        // admin's OWN household; IgnoreQueryFilters is what lets this sum every household's rows instead of
        // just theirs. The WHERE bounds the materialized set to the current month (a load bound — the
        // AiSpendRollup re-applies the window and is the correctness authority); one row per (household,
        // active day), so a month is a small set. Aggregated in Core so the split is unit-tested.
        var rows = await db.AiUsages.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Day >= monthStart)
            .ToListAsync(ct);

        return AiSpendRollup.Summarize(rows, today);
    }

    private async Task RequireAdminAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        if (!admin.Value.IsAdmin(state.User))
            throw new UnauthorizedAccessException("The operations view is only for the configured admin.");
    }
}
