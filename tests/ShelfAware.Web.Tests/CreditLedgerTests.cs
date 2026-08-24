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

    public CreditLedgerTests() => _ledger = new CreditLedger(_authDb);

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
}
