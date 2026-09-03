using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Billing;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>The scoped tier resolver behind the meter's Founder exemption and the Settings badge:
/// Founder reads as unlimited, everything else (including no signed-in household and a vanished one)
/// reads as Free — never an accidental grant of unlimited AI — and the tier is cached per scope.</summary>
public class EntitlementsTests : IDisposable
{
    private readonly TestAuthDb _auth = new();

    public void Dispose() => _auth.Dispose();

    /// <summary>Answers whatever household id the test wants (or none), so Entitlements can be exercised
    /// without a circuit — ICurrentHousehold's real resolution is its own tested concern.</summary>
    private sealed class FixedHousehold(string? id) : ICurrentHousehold
    {
        public ValueTask<string?> GetIdAsync(CancellationToken ct = default) => new(id);
        public ValueTask<string> GetRequiredIdAsync(CancellationToken ct = default) => new(id!);
        public void UseFixed(string householdId) { }
    }

    /// <summary>Throws on the FIRST context open (a transient auth.db error), then delegates — so a test
    /// can prove the fail-safe path both fails safe AND doesn't poison the per-scope cache.</summary>
    private sealed class FailOnceAuthFactory(IDbContextFactory<AuthDbContext> inner) : IDbContextFactory<AuthDbContext>
    {
        private bool _failed;

        public AuthDbContext CreateDbContext() => inner.CreateDbContext();

        public Task<AuthDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException("transient auth.db error");
            }
            return inner.CreateDbContextAsync(ct);
        }
    }

    private async Task<string> SeedHouseholdAsync(HouseholdTier tier, DateTimeOffset? renewsAt = null)
    {
        await using var db = _auth.CreateDbContext();
        var household = new Household { Name = "Test", Tier = tier, SubscriptionRenewsAt = renewsAt };
        db.Households.Add(household);
        await db.SaveChangesAsync();
        return household.Id;
    }

    private Entitlements For(string? householdId, bool paymentsEnabled = true) =>
        new(new FixedHousehold(householdId), _auth,
            new CreditLedger(_auth, Microsoft.Extensions.Options.Options.Create(new ShelfAware.Core.Billing.BillingOptions())),
            Microsoft.Extensions.Options.Options.Create(new ShelfAware.Web.Billing.PaymentsOptions { Enabled = paymentsEnabled }),
            NullLogger<Entitlements>.Instance);

    [Fact]
    public async Task A_founder_household_is_unlimited()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Founder);

        var tier = await For(id).GetTierAsync();

        Assert.Equal(HouseholdTier.Founder, tier);
        Assert.True(tier.IsUnlimited());
    }

    [Fact]
    public async Task A_free_household_is_not_unlimited()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Free);

        var tier = await For(id).GetTierAsync();

        Assert.Equal(HouseholdTier.Free, tier);
        Assert.False(tier.IsUnlimited());
    }

    [Fact]
    public async Task No_signed_in_household_reads_as_free()
    {
        var tier = await For(null).GetTierAsync();

        Assert.Equal(HouseholdTier.Free, tier);
        Assert.False(tier.IsUnlimited());
    }

    [Fact]
    public async Task An_unknown_household_reads_as_free_not_unlimited()
    {
        // A household id with no row (removed between sign-in and now): the safe default. A missing
        // household must never resolve to unlimited AI.
        var tier = await For("does-not-exist").GetTierAsync();

        Assert.Equal(HouseholdTier.Free, tier);
    }

    [Fact]
    public async Task A_transient_error_reads_as_free_and_is_not_cached()
    {
        // The fail-SAFE path: a tier we couldn't read is Free (limits apply), NEVER unlimited off a
        // transient auth.db error — and deliberately NOT cached, so a later call in the same scope
        // can still succeed. (The code review proved this by probe; this pins it against a later
        // "improvement" that caches the error.)
        var id = await SeedHouseholdAsync(HouseholdTier.Founder);
        var flaky = new FailOnceAuthFactory(_auth);
        var entitlements = new Entitlements(new FixedHousehold(id), flaky,
            new CreditLedger(_auth, Microsoft.Extensions.Options.Options.Create(new ShelfAware.Core.Billing.BillingOptions())),
            Microsoft.Extensions.Options.Options.Create(new ShelfAware.Web.Billing.PaymentsOptions { Enabled = true }),
            NullLogger<Entitlements>.Instance);

        // First call: the factory throws → Free, and the errored result is not cached.
        Assert.Equal(HouseholdTier.Free, await entitlements.GetTierAsync());

        // The recovered call reads the REAL tier — which only works if the error wasn't cached.
        Assert.Equal(HouseholdTier.Founder, await entitlements.GetTierAsync());
    }

    [Fact]
    public async Task The_tier_is_resolved_once_and_cached_for_the_scope()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Founder);
        var entitlements = For(id);
        Assert.Equal(HouseholdTier.Founder, await entitlements.GetTierAsync()); // caches Founder

        // Change the stored tier out from under the cached instance; a grant/revoke is meant to bite on
        // the household's NEXT scope, not mid-request, so this instance keeps what it resolved.
        await using (var db = _auth.CreateDbContext())
        {
            var household = await db.Households.SingleAsync(h => h.Id == id);
            household.Tier = HouseholdTier.Free;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HouseholdTier.Founder, await entitlements.GetTierAsync()); // still the cached value
    }

    // ---- The credit balance + the AI-allowed gate predicate (phase 4a) ----

    private static readonly long Allowance = AiPricing.MonthlyAllowanceRetailMicros(new BillingOptions());

    private CreditLedger Ledger() =>
        new(_auth, Microsoft.Extensions.Options.Options.Create(new BillingOptions()));

    [Fact]
    public async Task Get_balance_grants_an_Aware_household_its_lazy_allowance()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));

        var balance = await For(id).GetBalanceMicrosAsync();

        Assert.Equal(Allowance, balance); // reading the balance ran the lazy per-period grant
    }

    [Fact]
    public async Task No_signed_in_household_has_no_balance_and_no_AI()
    {
        Assert.Equal(0, await For(null).GetBalanceMicrosAsync());
        Assert.False(await For(null).IsAiAllowedAsync());
    }

    [Fact]
    public async Task A_founder_is_allowed_regardless_of_balance()
    {
        // No subscription period → no allowance, zero balance — but Founder is unlimited, so the balance
        // is never consulted.
        var id = await SeedHouseholdAsync(HouseholdTier.Founder);

        Assert.True(await For(id).IsAiAllowedAsync());
    }

    [Fact]
    public async Task A_free_household_is_allowed_only_with_a_positive_balance()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Free);
        Assert.False(await For(id).IsAiAllowedAsync()); // no grant, no balance

        await Ledger().GrantAsync(id, 500_000, "Welcome grant");
        Assert.True(await For(id).IsAiAllowedAsync()); // a leftover welcome grant is enough
    }

    [Fact]
    public async Task An_Aware_household_is_allowed_via_its_lazy_allowance()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));

        Assert.True(await For(id).IsAiAllowedAsync()); // the allowance is granted as the balance is checked
    }

    [Fact]
    public async Task With_billing_off_a_household_is_allowed_even_with_no_balance()
    {
        // ⚠️ §7: a managed box with no Payments config (self-host / dev / the family box) does NOT apply the
        // credit system — managed AI is unlimited by default. This is what keeps the gate from walling a box
        // that has a server key (which alone makes CircuitAiSettings.Managed true) but never enabled billing.
        var id = await SeedHouseholdAsync(HouseholdTier.Free);

        Assert.False(await For(id, paymentsEnabled: true).IsAiAllowedAsync());  // billing ON → Free with no credit is gated
        Assert.True(await For(id, paymentsEnabled: false).IsAiAllowedAsync());  // billing OFF → unlimited by default
    }
}
