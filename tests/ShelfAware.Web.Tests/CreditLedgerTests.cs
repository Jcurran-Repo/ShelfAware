using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Billing;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>The append-only credit ledger: balance is the SUM of a household's entries, consumption is
/// stored negative, a free call records nothing, and every read/write is hand-scoped per household
/// (auth.db has no query filter). The welcome-grant factory uses the configured amount.</summary>
public class CreditLedgerTests : IDisposable
{
    private readonly TestAuthDb _authDb = new();
    private readonly CreditLedger _ledger;

    public CreditLedgerTests() => _ledger = new CreditLedger(_authDb, Microsoft.Extensions.Options.Options.Create(new BillingOptions()));

    public void Dispose() => _authDb.Dispose();

    [Fact]
    public async Task An_empty_ledger_reads_zero()
    {
        Assert.Equal(0, await _ledger.GetBalanceMicrosAsync("hh-a"));
    }

    [Fact]
    public async Task Balance_is_a_grant_minus_its_consumption()
    {
        await _ledger.GrantAsync("hh-a", 1_650_000, "Welcome grant");
        await _ledger.RecordConsumptionAsync("hh-a", 250_000, "chat");
        await _ledger.RecordConsumptionAsync("hh-a", 100_000, "extraction");

        Assert.Equal(1_300_000, await _ledger.GetBalanceMicrosAsync("hh-a")); // 1,650,000 − 250,000 − 100,000
    }

    [Fact]
    public async Task Consumption_is_stored_negative()
    {
        await _ledger.RecordConsumptionAsync("hh-a", 250_000, "chat");

        await using var db = _authDb.CreateDbContext();
        var entry = await db.CreditLedger.SingleAsync();
        Assert.Equal(CreditEntryKind.Consumption, entry.Kind);
        Assert.Equal(-250_000, entry.AmountMicros); // stored negative so the SUM is the balance
    }

    [Fact]
    public async Task A_free_or_cached_call_records_nothing()
    {
        await _ledger.RecordConsumptionAsync("hh-a", 0, "cache hit");
        await _ledger.RecordConsumptionAsync("hh-a", -5, "bug");

        Assert.Equal(0, await _ledger.GetBalanceMicrosAsync("hh-a"));
        await using var db = _authDb.CreateDbContext();
        Assert.Empty(await db.CreditLedger.ToListAsync());
    }

    [Fact]
    public async Task The_ledger_is_hand_scoped_per_household()
    {
        await _ledger.GrantAsync("hh-a", 1_000_000, "Welcome grant");
        await _ledger.RecordConsumptionAsync("hh-a", 400_000, "chat");
        await _ledger.GrantAsync("hh-b", 500_000, "Welcome grant");

        Assert.Equal(600_000, await _ledger.GetBalanceMicrosAsync("hh-a")); // untouched by hh-b
        Assert.Equal(500_000, await _ledger.GetBalanceMicrosAsync("hh-b")); // untouched by hh-a's consumption
    }

    [Fact]
    public void The_welcome_grant_factory_uses_the_configured_amount()
    {
        var entry = CreditLedger.WelcomeGrant("hh-a", new BillingOptions());

        Assert.NotNull(entry);
        Assert.Equal(CreditEntryKind.Grant, entry!.Kind);
        Assert.Equal(AiPricing.WelcomeGrantRetailMicros(new BillingOptions()), entry.AmountMicros); // $1 × 1.65 = 1,650,000
        Assert.Equal("hh-a", entry.HouseholdId);
    }

    [Fact]
    public void A_zero_configured_welcome_grant_yields_no_entry()
    {
        // An operator who sets the grant to 0 shouldn't mint an empty ledger row on every signup.
        var entry = CreditLedger.WelcomeGrant("hh-a", new BillingOptions { WelcomeGrantDollars = 0m });

        Assert.Null(entry);
    }

    // ---- The lazy per-period Aware allowance + no-rollover (phase 4a, §4) ----

    private static readonly long Allowance = AiPricing.MonthlyAllowanceRetailMicros(new BillingOptions()); // $1 × 1.65

    private async Task<string> SeedHouseholdAsync(HouseholdTier tier, DateTimeOffset? renewsAt)
    {
        await using var db = _authDb.CreateDbContext();
        var h = new Household { Name = "Test", Tier = tier, SubscriptionRenewsAt = renewsAt };
        db.Households.Add(h);
        await db.SaveChangesAsync();
        return h.Id;
    }

    private async Task SetRenewsAtAsync(string householdId, DateTimeOffset renewsAt)
    {
        await using var db = _authDb.CreateDbContext();
        var h = await db.Households.SingleAsync(x => x.Id == householdId);
        h.SubscriptionRenewsAt = renewsAt;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_Aware_household_gets_the_monthly_allowance_on_first_check()
    {
        var period = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var id = await SeedHouseholdAsync(HouseholdTier.Aware, period);

        await _ledger.EnsureCurrentAllowanceAsync(id);

        Assert.Equal(Allowance, await _ledger.GetBalanceMicrosAsync(id));
        await using var db = _authDb.CreateDbContext();
        Assert.Equal(period, (await db.Households.SingleAsync()).AllowanceGrantedForPeriod); // marker set
    }

    [Fact]
    public async Task The_allowance_is_idempotent_within_a_period()
    {
        var period = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var id = await SeedHouseholdAsync(HouseholdTier.Aware, period);

        await _ledger.EnsureCurrentAllowanceAsync(id);
        await _ledger.EnsureCurrentAllowanceAsync(id);
        await _ledger.EnsureCurrentAllowanceAsync(id);

        Assert.Equal(Allowance, await _ledger.GetBalanceMicrosAsync(id)); // one grant, not three
        await using var db = _authDb.CreateDbContext();
        Assert.Single(await db.CreditLedger.Where(e => e.Kind == CreditEntryKind.Allowance).ToListAsync());
    }

    [Fact]
    public async Task A_non_Aware_household_gets_no_allowance()
    {
        var period = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var free = await SeedHouseholdAsync(HouseholdTier.Free, period);
        var founder = await SeedHouseholdAsync(HouseholdTier.Founder, period);

        await _ledger.EnsureCurrentAllowanceAsync(free);
        await _ledger.EnsureCurrentAllowanceAsync(founder);

        Assert.Equal(0, await _ledger.GetBalanceMicrosAsync(free));
        Assert.Equal(0, await _ledger.GetBalanceMicrosAsync(founder));
    }

    [Fact]
    public async Task An_Aware_household_with_no_period_gets_no_allowance()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware, renewsAt: null);

        await _ledger.EnsureCurrentAllowanceAsync(id);

        Assert.Equal(0, await _ledger.GetBalanceMicrosAsync(id));
    }

    [Fact]
    public async Task A_new_period_expires_the_prior_unspent_allowance_and_grants_a_fresh_one()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        await _ledger.EnsureCurrentAllowanceAsync(id);                // period 1: +A
        await _ledger.RecordConsumptionAsync(id, 400_000, "chat");    // spend part of it → A − 400k

        await SetRenewsAtAsync(id, DateTimeOffset.Parse("2026-11-01T00:00:00Z"));
        await _ledger.EnsureCurrentAllowanceAsync(id);               // period 2: expire the unspent A−400k, grant +A

        // No rollover: the unspent A−400k is swept, so the balance is exactly one fresh allowance.
        Assert.Equal(Allowance, await _ledger.GetBalanceMicrosAsync(id));
    }

    [Fact]
    public async Task Purchases_survive_the_allowance_expiry_when_consumption_dipped_into_them()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        await _ledger.GrantAsync(id, 2_000_000, "credit pack");      // persisting money (a Grant persists like a Purchase)
        await _ledger.EnsureCurrentAllowanceAsync(id);              // +A
        await _ledger.RecordConsumptionAsync(id, 2_000_000, "big"); // spends all of A, then 350k of the persisting money

        await SetRenewsAtAsync(id, DateTimeOffset.Parse("2026-11-01T00:00:00Z"));
        await _ledger.EnsureCurrentAllowanceAsync(id);              // A fully spent → expire 0; grant +A

        // Spend-allowance-first: consumption drew all of A, then the 350k OVERFLOW dipped the pack. So the
        // pack keeps 2,000,000 − 350,000, and the expiry sweeps nothing (A was fully spent); plus the fresh A.
        var overflow = 2_000_000 - Allowance; // consumption beyond the allowance
        Assert.Equal((2_000_000 - overflow) + Allowance, await _ledger.GetBalanceMicrosAsync(id));
    }
}
