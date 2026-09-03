using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Billing;

namespace ShelfAware.Web.Data;

/// <summary>Resolves what the current scope's household is entitled to. See <see cref="Entitlements"/>.</summary>
public interface IEntitlements
{
    /// <summary>The current household's tier, or <see cref="HouseholdTier.Free"/> when there is no
    /// signed-in household or the tier can't be read (the safe default — never unlimited by accident).</summary>
    ValueTask<HouseholdTier> GetTierAsync(CancellationToken cancellationToken = default);

    /// <summary>The current household's credit balance in retail micros, read FRESH each call (never the
    /// per-circuit tier cache — a balance changes on every AI call). The lazy monthly allowance is ensured
    /// first, so an Aware subscriber's current-period grant is reflected. Zero when there's no signed-in
    /// household.</summary>
    ValueTask<long> GetBalanceMicrosAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the current household may make a managed AI call. Allowed when billing is OFF on this
    /// deployment (self-host / dev / the family box — §7 "unlimited by default"; the credit system only bites
    /// where <c>Payments:Enabled</c>), OR the tier <see cref="HouseholdTierExtensions.IsUnlimited"/> (Founder),
    /// OR a positive credit balance. The gate (phase 4b) consults this before a metered call. Read fresh via
    /// <see cref="GetBalanceMicrosAsync"/>.</summary>
    ValueTask<bool> IsAiAllowedAsync(CancellationToken cancellationToken = default);
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
///
/// ⚠️ Cached per SCOPE, and a Blazor Server scope is the CIRCUIT, which lives as long as the SignalR
/// connection — a left-open tab can be HOURS. So a grant/revoke bites on the household's NEXT circuit,
/// not mid-session: a newly-granted Founder stays capped until they reconnect (safe), and a REVOKED
/// Founder keeps unlimited until their circuit ends (the one staleness in the host's-wallet direction —
/// bounded, still recorded, acceptable for a rare operator-granted trust tier where revocation is
/// non-adversarial). ⚠️ Phase 2's live credit BALANCE must NOT inherit this per-circuit cache: a
/// balance changes on every call, so caching it for a circuit's lifetime would let one long session
/// overspend. The live-read SEAM is here; the caching CONTRACT is per-value — the balance's is "read
/// fresh each check", not "cache for the scope".
/// </summary>
public sealed class Entitlements(
    ICurrentHousehold currentHousehold,
    IDbContextFactory<AuthDbContext> authDb,
    CreditLedger ledger,
    IOptions<PaymentsOptions> payments,
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

    public async ValueTask<long> GetBalanceMicrosAsync(CancellationToken cancellationToken = default)
    {
        var householdId = await currentHousehold.GetIdAsync(cancellationToken);
        if (householdId is null) return 0;
        // Lazy per-month grant runs first (idempotent within the month), so an Aware subscriber's current
        // allowance is in the balance we then read. NOT cached — the balance changes on every AI call.
        // named cancellationToken: — EnsureCurrentAllowanceAsync's optional `now` (DateTimeOffset?) sits
        // before the token, so a positional token would fail to bind; we want UtcNow, so skip `now` by name.
        await ledger.EnsureCurrentAllowanceAsync(householdId, cancellationToken: cancellationToken);
        return await ledger.GetBalanceMicrosAsync(householdId, cancellationToken);
    }

    public async ValueTask<bool> IsAiAllowedAsync(CancellationToken cancellationToken = default)
    {
        // Billing OFF on this deployment → the credit system doesn't apply; managed AI is unlimited by
        // default (self-host / dev / family box — §7). This is what keeps the gate from walling a box that
        // has a server key but no Payments config (a key alone makes CircuitAiSettings.Managed true).
        if (!payments.Value.IsConfigured) return true;
        // Founder is unlimited (skip the balance entirely); everyone else needs credit left.
        if ((await GetTierAsync(cancellationToken)).IsUnlimited()) return true;
        return await GetBalanceMicrosAsync(cancellationToken) > 0;
    }
}
