using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Data;

/// <summary>Resolves what the current scope's household is entitled to. See <see cref="Entitlements"/>.</summary>
public interface IEntitlements
{
    /// <summary>The current household's tier, or <see cref="HouseholdTier.Free"/> when there is no
    /// signed-in household or the tier can't be read (the safe default — never unlimited by accident).</summary>
    ValueTask<HouseholdTier> GetTierAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Scoped. The current household's tier, resolved once per circuit/request and cached — the AI meter
/// asks it on the hot path, so it touches auth.db at most once per scope (and only when a daily limit
/// is actually configured; see <see cref="Services.AiUsageMeter"/>, which returns before consulting
/// this on a no-limit deployment).
///
/// This is a LIVE read, not a cookie claim, on purpose. Founder status alone is stable enough to ride
/// in a claim, but the subscription work that builds on this (docs/subscription-plan.md) makes the
/// entitlement tier-AND-live-credit-balance, and a balance changes on every AI call — it can never be
/// a claim. Building the live-read seam now is what lets phase 2 EXTEND this service (add the balance)
/// rather than rip out a claim and replace it.
/// </summary>
public sealed class Entitlements(
    ICurrentHousehold currentHousehold,
    IDbContextFactory<AuthDbContext> authDb,
    ILogger<Entitlements> logger) : IEntitlements
{
    private HouseholdTier? _cached;

    public async ValueTask<HouseholdTier> GetTierAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached) return cached;

        var householdId = await currentHousehold.GetIdAsync(cancellationToken);
        if (householdId is null)
        {
            // No signed-in household (a pre-auth scope, or background work that never pinned one):
            // the default tier, which is not unlimited. Cache it — the scope's identity won't change.
            _cached = HouseholdTier.Free;
            return HouseholdTier.Free;
        }

        try
        {
            await using var db = await authDb.CreateDbContextAsync(cancellationToken);
            // auth.db has no query filter (it's operator/identity space), so this is an ordinary
            // scoped-by-id read — no IgnoreQueryFilters, unlike the pantry-side admin reader.
            var tier = await db.Households.AsNoTracking()
                .Where(h => h.Id == householdId)
                .Select(h => (HouseholdTier?)h.Tier)
                .SingleOrDefaultAsync(cancellationToken);
            _cached = tier ?? HouseholdTier.Free; // a vanished household reads as Free, not unlimited
            return _cached.Value;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Fail SAFE: a tier we couldn't read is treated as Free (limits apply), never unlimited off
            // a transient auth.db error — the gate exists to protect the host's wallet. Deliberately not
            // cached, so a later call in the same scope can still succeed.
            logger.LogError(ex, "Couldn't resolve the household tier; treating it as Free (limits apply).");
            return HouseholdTier.Free;
        }
    }
}
