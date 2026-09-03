using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ShelfAware.Web.Auth;

/// <summary>The demo box's daily account-creation cap (docs/subscription-plan.md §10): a box-wide bound on
/// how many NEW households appear per day, so a public box on the host's key can't have free households
/// created into it without limit.
///
/// Counts <see cref="AppUser.CreatedOn"/> rows stamped with today (server-local, the app's universal
/// "today"), so the number IS reality — there is no separate counter to increment, keep in sync, or
/// under-count from a forgotten call site. A slight overshoot under two simultaneous registrations (both
/// read the same count, both proceed) is harmless for a soft brake and fails toward MORE-permissive by at
/// most the concurrency, which on a demo box is ~0.
///
/// Scoped like <see cref="HouseholdService"/>: it reads the same request-scoped <see cref="AuthDbContext"/>
/// the registration flow uses.</summary>
public sealed class AccountCreationLimiter(AuthDbContext db, IOptions<AuthOptions> options)
{
    /// <summary>True when today's account creations have reached the configured cap — the registration
    /// page then refuses a NEW household with <see cref="DemoLimits.DailyCapReachedMessage"/>. Always false
    /// when no cap is configured (the self-host/family default), so those boxes pay nothing.</summary>
    public async Task<bool> AtDailyLimitAsync(CancellationToken ct = default)
    {
        if (options.Value.DailyAccountCreationLimit is not int limit) return false;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var createdToday = await db.Users.CountAsync(u => u.CreatedOn == today, ct);
        return createdToday >= limit;
    }
}
