using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    private async Task<string> SeedHouseholdAsync(HouseholdTier tier)
    {
        await using var db = _auth.CreateDbContext();
        var household = new Household { Name = "Test", Tier = tier };
        db.Households.Add(household);
        await db.SaveChangesAsync();
        return household.Id;
    }

    private Entitlements For(string? householdId) =>
        new(new FixedHousehold(householdId), _auth, NullLogger<Entitlements>.Instance);

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
}
