using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Data;

/// <summary>One household on the admin roster: its name, members (emails, for the operator to
/// recognise who's who), tier, and — when it's a Founder — when that was granted.</summary>
public sealed record AdminHousehold(
    string Id, string Name, HouseholdTier Tier, DateTimeOffset? FounderSince, IReadOnlyList<string> MemberEmails);

/// <summary>
/// The admin's household roster (who's on this deployment and what tier they're on) and the Founder
/// grant/revoke. Both admin-gated inside the service against <see cref="AdminOptions.IsAdmin"/> — the
/// routed /admin page already carries the Admin policy, so this is defense in depth and the layer a
/// directly-rendered component test can pin, exactly as <see cref="AdminReportReader"/> /
/// <see cref="ReportResolutionService"/> do.
///
/// Unlike those two, this reads and writes AUTH.db, which has no tenancy query filter (it's
/// identity/operator space) — so the roster read is an ordinary read (no IgnoreQueryFilters) and the
/// grant is an ordinary column-scoped write. Reader and writer share one class here because the
/// dangerous cross-tenancy capability the report reader carries (its IgnoreQueryFilters) simply
/// isn't present on this side, so there's nothing to keep provably read-only.
/// </summary>
public sealed class AdminHouseholdService(
    IDbContextFactory<AuthDbContext> authDb,
    AuthenticationStateProvider auth,
    IOptions<AdminOptions> admin)
{
    /// <summary>Every household, name-ordered, each with its members and tier — the operator's view of
    /// "who's here and what are they entitled to". Deliberately unbounded: households are one-per-
    /// family/subscriber, not per-event like the error/report logs, so there's no churning tail to cap;
    /// a public deployment large enough to need paging is a later phase's concern.</summary>
    public async Task<IReadOnlyList<AdminHousehold>> ListAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync();
        await using var db = await authDb.CreateDbContextAsync(ct);

        var households = await db.Households.AsNoTracking()
            .OrderBy(h => h.Name)
            .Select(h => new { h.Id, h.Name, h.Tier, h.FounderSince })
            .ToListAsync(ct);

        // Members separately, then grouped in memory — AppUser carries HouseholdId as a plain value
        // (indexed), with no navigation to include. Same shape as AdminReportReader's name lookup.
        var members = await db.Users.AsNoTracking()
            .Where(u => u.HouseholdId != null)
            .OrderBy(u => u.Email)
            .Select(u => new { u.HouseholdId, Email = u.Email ?? u.UserName ?? "(unknown)" })
            .ToListAsync(ct);
        var byHousehold = members.ToLookup(m => m.HouseholdId!, m => m.Email);

        return households
            .Select(h => new AdminHousehold(h.Id, h.Name, h.Tier, h.FounderSince, byHousehold[h.Id].ToList()))
            .ToList();
    }

    /// <summary>Grant or revoke the Founder tier for a household. Returns false when no such household
    /// exists (deleted since the roster was rendered). ⚠️ No CancellationToken, on purpose — item 38's
    /// write rule: a grant is a one-shot write, and threading a page token would let a navigate-away
    /// tear it down mid-flight with no message and no retry surface.
    ///
    /// A column-scoped ExecuteUpdate, so nothing but Tier/FounderSince can ride along. Granting stamps
    /// FounderSince only when it's null (COALESCE), so re-granting an existing Founder keeps its
    /// original date; revoking clears both. This is the ONLY write path to Tier — there is deliberately
    /// no self-service one, which is what makes the tier un-self-grantable.</summary>
    public async Task<bool> SetFounderAsync(string householdId, bool founder)
    {
        await RequireAdminAsync();
        await using var db = await authDb.CreateDbContextAsync(CancellationToken.None);

        if (founder)
        {
            var now = DateTimeOffset.Now;
            return await db.Households
                .Where(h => h.Id == householdId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Tier, HouseholdTier.Founder)
                    .SetProperty(h => h.FounderSince, h => h.FounderSince ?? now), CancellationToken.None) > 0;
        }

        return await db.Households
            .Where(h => h.Id == householdId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.Tier, HouseholdTier.Free)
                .SetProperty(h => h.FounderSince, (DateTimeOffset?)null), CancellationToken.None) > 0;
    }

    private async Task RequireAdminAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        if (!admin.Value.IsAdmin(state.User))
            throw new UnauthorizedAccessException("The household roster is only for the configured admin.");
    }
}
