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

    // ---- The lazy per-CALENDAR-MONTH Aware allowance + no-rollover (phase 4a/4d, §4) ----

    private static readonly long Allowance = AiPricing.MonthlyAllowanceRetailMicros(new BillingOptions()); // $1 × 1.65
    private static readonly DateTimeOffset Oct = DateTimeOffset.Parse("2026-10-15T09:00:00Z"); // an instant in October
    private static readonly DateTimeOffset Nov = DateTimeOffset.Parse("2026-11-15T09:00:00Z"); // the next calendar month
    private static readonly DateTimeOffset Dec = DateTimeOffset.Parse("2026-12-15T09:00:00Z"); // and the one after

    private async Task<string> SeedHouseholdAsync(HouseholdTier tier, DateTimeOffset? renewsAt = null)
    {
        await using var db = _authDb.CreateDbContext();
        var h = new Household { Name = "Test", Tier = tier, SubscriptionRenewsAt = renewsAt };
        db.Households.Add(h);
        await db.SaveChangesAsync();
        return h.Id;
    }

    // A ledger over the SAME auth.db with a specific monthly-allowance amount — for the "operator paused the
    // allowance (MonthlyAllowanceDollars: 0)" scenario, which is where a month grants nothing.
    private CreditLedger LedgerWithAllowance(decimal dollars) =>
        new(_authDb, Microsoft.Extensions.Options.Options.Create(new BillingOptions { MonthlyAllowanceDollars = dollars }));

    [Fact]
    public async Task An_Aware_household_gets_the_monthly_allowance_on_first_check()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);

        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);

        Assert.Equal(Allowance, await _ledger.GetBalanceMicrosAsync(id));
        await using var db = _authDb.CreateDbContext();
        // The marker is the CALENDAR MONTH, not the subscription's renewal date.
        Assert.Equal(CreditLedger.PeriodFor(Oct), (await db.Households.SingleAsync()).AllowanceGrantedForPeriod);
    }

    [Fact]
    public async Task The_allowance_is_idempotent_within_a_month()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);

        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct.AddDays(5));  // same month, a later day
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct.AddDays(12));

        Assert.Equal(Allowance, await _ledger.GetBalanceMicrosAsync(id)); // one grant, not three
        await using var db = _authDb.CreateDbContext();
        Assert.Single(await db.CreditLedger.Where(e => e.Kind == CreditEntryKind.Allowance).ToListAsync());
    }

    [Fact]
    public async Task The_allowance_drips_each_calendar_month_even_on_annual_billing()
    {
        // ⚠️ The #1 regression pin: an ANNUAL subscriber renews once a year, but the grant must still drip
        // MONTHLY (§4). Keying the period on the calendar month (not SubscriptionRenewsAt) is what makes a
        // second grant appear next month even though the renewal date is a year out. With the old code
        // (period == SubscriptionRenewsAt), month 2 saw the marker already == the annual date and granted
        // nothing — one allowance per YEAR.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware, renewsAt: DateTimeOffset.Parse("2027-10-01T00:00:00Z"));

        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);   // month 1
        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);   // month 2 — a fresh grant despite the annual renewal

        await using var db = _authDb.CreateDbContext();
        Assert.Equal(2, await db.CreditLedger.CountAsync(e => e.Kind == CreditEntryKind.Allowance));
    }

    [Fact]
    public async Task An_Aware_household_gets_the_allowance_regardless_of_its_renewal_date()
    {
        // Tier == Aware IS the active-subscription signal (the webhook drops it to Free on cancel), so a
        // missing SubscriptionRenewsAt must not withhold the monthly allowance.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware, renewsAt: null);

        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);

        Assert.Equal(Allowance, await _ledger.GetBalanceMicrosAsync(id));
    }

    [Fact]
    public async Task A_non_Aware_household_gets_no_allowance()
    {
        var free = await SeedHouseholdAsync(HouseholdTier.Free);
        var founder = await SeedHouseholdAsync(HouseholdTier.Founder);

        await _ledger.EnsureCurrentAllowanceAsync(free, Oct);
        await _ledger.EnsureCurrentAllowanceAsync(founder, Oct);

        Assert.Equal(0, await _ledger.GetBalanceMicrosAsync(free));
        Assert.Equal(0, await _ledger.GetBalanceMicrosAsync(founder));
    }

    [Fact]
    public async Task A_new_month_expires_the_prior_unspent_allowance_and_grants_a_fresh_one()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);           // month 1: +A
        await _ledger.RecordConsumptionAsync(id, 400_000, "chat");    // spend part of it → A − 400k

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);          // month 2: expire the unspent A−400k, grant +A

        // No rollover: the unspent A−400k is swept, so the balance is exactly one fresh allowance. (Also the
        // #7 guard against dropping the Consumption term — then the full A would be swept, leaving A−400k.)
        Assert.Equal(Allowance, await _ledger.GetBalanceMicrosAsync(id));
    }

    [Fact]
    public async Task Purchases_survive_the_allowance_expiry_when_consumption_dipped_into_them()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.GrantAsync(id, 2_000_000, "credit pack");      // persisting money (a Grant persists like a Purchase)
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);         // +A
        await _ledger.RecordConsumptionAsync(id, 2_000_000, "big"); // spends all of A, then the overflow dips the pack

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);         // A fully spent → expire 0; grant +A

        // Spend-allowance-first: consumption drew all of A, then the OVERFLOW dipped the pack. So the pack
        // keeps 2,000,000 − overflow, the expiry sweeps nothing (A was fully spent), plus the fresh A.
        var overflow = 2_000_000 - Allowance;
        Assert.Equal((2_000_000 - overflow) + Allowance, await _ledger.GetBalanceMicrosAsync(id));
    }

    [Fact]
    public async Task Consumption_before_the_allowance_is_not_counted_against_it()
    {
        // #7 guard (the `e.Id > lastAllowance.Id` term): only consumption AFTER the allowance draws it down.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.GrantAsync(id, 2_000_000, "credit pack");
        await _ledger.RecordConsumptionAsync(id, 500_000, "before"); // drawn from the pack, BEFORE any allowance
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);         // +A (nothing consumed against it yet)

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);         // month 2: A fully unspent → sweep A; +A

        // The pre-allowance 500k is NOT counted against A, so the full A is swept; the pack keeps 1.5M.
        Assert.Equal((2_000_000 - 500_000) + Allowance, await _ledger.GetBalanceMicrosAsync(id));
    }

    [Fact]
    public async Task A_purchase_after_the_allowance_is_not_swept()
    {
        // #7 guard (the Kind filter): the sweep draws down only Consumption + Expiry, NEVER a Purchase/Grant —
        // a pack bought after the allowance must survive the month rollover intact (dropping the filter would
        // add the +5M pack into "drawn", compute a huge unspent, and sweep the pack).
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);         // +A
        await _ledger.GrantAsync(id, 5_000_000, "credit pack");     // pack AFTER the allowance, no consumption

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);         // sweep the unspent A; +A

        Assert.Equal(5_000_000 + Allowance, await _ledger.GetBalanceMicrosAsync(id)); // pack untouched + fresh A
    }

    [Fact]
    public async Task A_month_that_grants_nothing_does_not_re_sweep_a_prior_expiry()
    {
        // ⚠️ The #6 double-sweep guard. If the operator pauses the allowance (MonthlyAllowanceDollars: 0), a
        // later month posts an Expiry but NO new Allowance — so the old allowance stays "the latest". Without
        // the Expiry term in the unspent calc it would be swept AGAIN every month, silently eating purchased
        // credit.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await LedgerWithAllowance(1m).EnsureCurrentAllowanceAsync(id, Oct); // month 1: +A (allowance on)
        await _ledger.GrantAsync(id, 5_000_000, "credit pack");            // a purchased pack

        var paused = LedgerWithAllowance(0m); // operator turns the allowance off
        await paused.EnsureCurrentAllowanceAsync(id, Nov);                 // month 2: sweep A once, grant nothing
        Assert.Equal(5_000_000, await _ledger.GetBalanceMicrosAsync(id)); // pack only (A swept exactly once)

        await paused.EnsureCurrentAllowanceAsync(id, Dec);                // month 3: must NOT re-sweep
        Assert.Equal(5_000_000, await _ledger.GetBalanceMicrosAsync(id)); // still the pack, not 5M − A
    }

    [Fact]
    public async Task The_sweep_reads_the_LATEST_allowance_across_several_months()
    {
        // #7 guard (OrderByDescending, not OrderBy): with several allowances on file, the sweep must measure
        // the most RECENT one. Three months of grant-and-partly-spend must each net to exactly one fresh
        // allowance — which only holds if "the last allowance" is the newest.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);         // +A1
        await _ledger.RecordConsumptionAsync(id, 300_000, "m1");
        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);         // sweep A1's unspent, +A2
        await _ledger.RecordConsumptionAsync(id, 300_000, "m2");
        await _ledger.EnsureCurrentAllowanceAsync(id, Dec);         // sweep A2's unspent, +A3

        Assert.Equal(Allowance, await _ledger.GetBalanceMicrosAsync(id)); // exactly one fresh allowance, no rollover
    }
}
