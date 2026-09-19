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
    public async Task A_consumption_that_landed_reports_landed_even_when_the_tidying_up_fails()
    {
        // ⚠️ The caller hands the action's ONE charge back when this throws, so a later round can pay
        // instead. That is right when nothing was written and catastrophic when something was: the retry
        // becomes a SECOND ledger line for one act, and the ledger is append-only with no idempotency key,
        // so nothing nets them. A connection that dies on dispose, AFTER the INSERT committed, is exactly
        // that shape — and only this method can tell which side of the commit a failure came from.
        var ledger = new CreditLedger(
            new DisposeThrowsAuthDbFactory(_authDb),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions()));

        Assert.NotNull(await ledger.RecordConsumptionAsync("hh-a", 2, "chat"));   // reported as charged
        Assert.Equal(-2, await _ledger.GetBalanceCreditsAsync("hh-a"));        // and it really is
    }

    /// <summary>An auth.db factory whose contexts throw on DISPOSE — after any write they were asked to do
    /// has already committed.</summary>
    private sealed class DisposeThrowsAuthDbFactory(IDbContextFactory<AuthDbContext> inner)
        : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => new ThrowsOnDispose(inner.CreateDbContext());
        public Task<AuthDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult<AuthDbContext>(new ThrowsOnDispose(inner.CreateDbContext()));
    }

    private sealed class ThrowsOnDispose : AuthDbContext
    {
        public ThrowsOnDispose(AuthDbContext real) : base(Options(real)) { }

        private static DbContextOptions<AuthDbContext> Options(AuthDbContext real)
        {
            var options = new DbContextOptionsBuilder<AuthDbContext>()
                .UseSqlite(real.Database.GetDbConnection()).Options;
            real.Dispose();
            return options;
        }

        public override ValueTask DisposeAsync() =>
            throw new InvalidOperationException("the connection died while being returned to the pool");
    }

    [Fact]
    public async Task An_empty_ledger_reads_zero()
    {
        Assert.Equal(0, await _ledger.GetBalanceCreditsAsync("hh-a"));
    }

    [Fact]
    public async Task Balance_is_a_grant_minus_its_consumption()
    {
        await _ledger.GrantAsync("hh-a", 165, "Welcome grant");
        await _ledger.RecordConsumptionAsync("hh-a", 25, "chat");
        await _ledger.RecordConsumptionAsync("hh-a", 10, "extraction");

        Assert.Equal(130, await _ledger.GetBalanceCreditsAsync("hh-a")); // 165 − 25 − 10
    }

    [Fact]
    public async Task Consumption_is_stored_negative()
    {
        await _ledger.RecordConsumptionAsync("hh-a", 25, "chat");

        await using var db = _authDb.CreateDbContext();
        var entry = await db.CreditLedger.SingleAsync();
        Assert.Equal(CreditEntryKind.Consumption, entry.Kind);
        Assert.Equal(-25, entry.AmountCredits); // stored negative so the SUM is the balance
    }

    [Fact]
    public async Task A_free_or_cached_call_records_nothing()
    {
        await _ledger.RecordConsumptionAsync("hh-a", 0, "cache hit");
        await _ledger.RecordConsumptionAsync("hh-a", -5, "bug");

        Assert.Equal(0, await _ledger.GetBalanceCreditsAsync("hh-a"));
        await using var db = _authDb.CreateDbContext();
        Assert.Empty(await db.CreditLedger.ToListAsync());
    }

    [Fact]
    public async Task The_ledger_is_hand_scoped_per_household()
    {
        await _ledger.GrantAsync("hh-a", 100, "Welcome grant");
        await _ledger.RecordConsumptionAsync("hh-a", 40, "chat");
        await _ledger.GrantAsync("hh-b", 50, "Welcome grant");

        Assert.Equal(60, await _ledger.GetBalanceCreditsAsync("hh-a")); // untouched by hh-b
        Assert.Equal(50, await _ledger.GetBalanceCreditsAsync("hh-b")); // untouched by hh-a's consumption
    }

    [Fact]
    public void The_welcome_grant_factory_uses_the_configured_amount()
    {
        var entry = CreditLedger.WelcomeGrant("hh-a", new BillingOptions());

        Assert.NotNull(entry);
        Assert.Equal(CreditEntryKind.Grant, entry!.Kind);
        Assert.Equal(CreditPricing.WelcomeGrantCredits(new BillingOptions()), entry.AmountCredits); // $1 of cost ÷ 1¢ a credit = 100 credits
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

    private static readonly long Allowance = CreditPricing.MonthlyAllowanceCredits(new BillingOptions()); // $1 of cost ÷ 1¢ a credit = 100
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

        Assert.Equal(Allowance, await _ledger.GetBalanceCreditsAsync(id));
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

        Assert.Equal(Allowance, await _ledger.GetBalanceCreditsAsync(id)); // one grant, not three
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

        Assert.Equal(Allowance, await _ledger.GetBalanceCreditsAsync(id));
    }

    [Fact]
    public async Task A_non_Aware_household_gets_no_allowance()
    {
        var free = await SeedHouseholdAsync(HouseholdTier.Free);
        var founder = await SeedHouseholdAsync(HouseholdTier.Founder);

        await _ledger.EnsureCurrentAllowanceAsync(free, Oct);
        await _ledger.EnsureCurrentAllowanceAsync(founder, Oct);

        Assert.Equal(0, await _ledger.GetBalanceCreditsAsync(free));
        Assert.Equal(0, await _ledger.GetBalanceCreditsAsync(founder));
    }

    [Fact]
    public async Task A_new_month_expires_the_prior_unspent_allowance_and_grants_a_fresh_one()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);           // month 1: +A
        await _ledger.RecordConsumptionAsync(id, 40, "chat");    // spend part of it → A − 40

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);          // month 2: expire the unspent A−40, grant +A

        // No rollover: the unspent A−40 is swept, so the balance is exactly one fresh allowance. (Also the
        // #7 guard against dropping the Consumption term — then the full A would be swept, leaving A−40.)
        Assert.Equal(Allowance, await _ledger.GetBalanceCreditsAsync(id));
    }

    [Fact]
    public async Task Purchases_survive_the_allowance_expiry_when_consumption_dipped_into_them()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.GrantAsync(id, 200, "credit pack");      // persisting money (a Grant persists like a Purchase)
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);         // +A
        await _ledger.RecordConsumptionAsync(id, 200, "big"); // spends all of A, then the overflow dips the pack

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);         // A fully spent → expire 0; grant +A

        // Spend-allowance-first: consumption drew all of A, then the OVERFLOW dipped the pack. So the pack
        // keeps 2,000,000 − overflow, the expiry sweeps nothing (A was fully spent), plus the fresh A.
        var overflow = 200 - Allowance;
        Assert.Equal((200 - overflow) + Allowance, await _ledger.GetBalanceCreditsAsync(id));
    }

    [Fact]
    public async Task Consumption_before_the_allowance_is_not_counted_against_it()
    {
        // #7 guard (the `e.Id > lastAllowance.Id` term): only consumption AFTER the allowance draws it down.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.GrantAsync(id, 200, "credit pack");
        await _ledger.RecordConsumptionAsync(id, 50, "before"); // drawn from the pack, BEFORE any allowance
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);         // +A (nothing consumed against it yet)

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);         // month 2: A fully unspent → sweep A; +A

        // The pre-allowance 50 is NOT counted against A, so the full A is swept; the pack keeps 150.
        Assert.Equal((200 - 50) + Allowance, await _ledger.GetBalanceCreditsAsync(id));
    }

    [Fact]
    public async Task A_purchase_after_the_allowance_is_not_swept()
    {
        // #7 guard (the Kind filter): the sweep draws down only Consumption + Expiry, NEVER a Purchase/Grant —
        // a pack bought after the allowance must survive the month rollover intact (dropping the filter would
        // add the +500 pack into "drawn", compute a huge unspent, and sweep the pack).
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);         // +A
        await _ledger.GrantAsync(id, 500, "credit pack");     // pack AFTER the allowance, no consumption

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);         // sweep the unspent A; +A

        Assert.Equal(500 + Allowance, await _ledger.GetBalanceCreditsAsync(id)); // pack untouched + fresh A
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
        await _ledger.GrantAsync(id, 500, "credit pack");            // a purchased pack

        var paused = LedgerWithAllowance(0m); // operator turns the allowance off
        await paused.EnsureCurrentAllowanceAsync(id, Nov);                 // month 2: sweep A once, grant nothing
        Assert.Equal(500, await _ledger.GetBalanceCreditsAsync(id)); // pack only (A swept exactly once)

        await paused.EnsureCurrentAllowanceAsync(id, Dec);                // month 3: must NOT re-sweep
        Assert.Equal(500, await _ledger.GetBalanceCreditsAsync(id)); // still the pack, not 500 − A
    }

    [Fact]
    public async Task The_sweep_reads_the_LATEST_allowance_across_several_months()
    {
        // #7 guard (OrderByDescending, not OrderBy): with several allowances on file, the sweep must measure
        // the most RECENT one. Three months of grant-and-partly-spend must each net to exactly one fresh
        // allowance — which only holds if "the last allowance" is the newest.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);         // +A1
        await _ledger.RecordConsumptionAsync(id, 30, "m1");
        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);         // sweep A1's unspent, +A2
        await _ledger.RecordConsumptionAsync(id, 30, "m2");
        await _ledger.EnsureCurrentAllowanceAsync(id, Dec);         // sweep A2's unspent, +A3

        Assert.Equal(Allowance, await _ledger.GetBalanceCreditsAsync(id)); // exactly one fresh allowance, no rollover
    }

    // ---- reversals: credits handed back for an act that was charged and did not deliver ----

    [Fact]
    public async Task A_reversal_puts_the_credits_back()
    {
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);
        var charge = await _ledger.RecordConsumptionAsync(id, 42, "A meal plan (124 meals)");
        Assert.NotNull(charge);

        Assert.True(await _ledger.ReverseConsumptionAsync(id, 39, "A meal plan — refunded", charge!.Value));

        Assert.Equal(Allowance - 3, await _ledger.GetBalanceCreditsAsync(id)); // kept the 7 meals it got
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_reversal_of_nothing_writes_nothing(long credits)
    {
        // The give-back is computed as charged − kept, and an act that delivered everything owes zero. A row
        // for it would be noise on the household's own record; a NEGATIVE one would be a second consumption
        // wearing a positive kind's name.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);

        var charge = await _ledger.RecordConsumptionAsync(id, 5, "something");

        Assert.False(await _ledger.ReverseConsumptionAsync(id, credits, "nothing owed", charge!.Value));

        Assert.Equal(Allowance - 5, await _ledger.GetBalanceCreditsAsync(id)); // the charge stands, untouched
        await using var db = _authDb.CreateDbContext();
        Assert.DoesNotContain(await db.CreditLedger.Where(e => e.HouseholdId == id).ToListAsync(),
            e => e.Kind == CreditEntryKind.Reversal);
    }

    [Fact]
    public async Task A_refunded_allowance_credit_still_expires_with_its_month()
    {
        // ⚠️ THE reason UnspentAllowanceCreditsAsync counts Reversal alongside Consumption and Expiry.
        // Reversal is POSITIVE where those two are negative, so it nets a consumption back out. Drop the
        // Reversal term and the sweep measures the allowance as 40 credits more spent than it is: it takes
        // only 58 instead of 97, and the 39 that came back rolls into a month it was never granted for —
        // a household could bank an allowance by provoking failures.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);                 // +A
        var charge = await _ledger.RecordConsumptionAsync(id, 42, "A meal plan");   // A − 42
        await _ledger.ReverseConsumptionAsync(id, 39, "refunded", charge!.Value);   // A − 3

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);                 // sweep A − 3, grant +A

        Assert.Equal(Allowance, await _ledger.GetBalanceCreditsAsync(id)); // exactly one fresh allowance
    }

    [Fact]
    public async Task A_reversal_of_a_charge_that_dipped_into_a_pack_unwinds_in_the_same_order()
    {
        // The reversal term within ONE period: consumption that dipped past the allowance into a pack, then
        // partly refunded. Spend-allowance-first, so the refund unwinds pack-first.
        // (Named for what it holds. It used to be called "…does not let the sweep reach purchased credit",
        // which is the defect in A_reversal_of_last_months_charge_… below — and this arrangement passes
        // whether or not that defect is present, so the name was a coverage claim it did not hold.)
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.GrantAsync(id, 50, "credit pack");
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);                  // +A, on top of the pack
        var charge = await _ledger.RecordConsumptionAsync(id, Allowance + 20, "big"); // all of A, then 20 of the pack
        await _ledger.ReverseConsumptionAsync(id, 39, "refunded", charge!.Value);     // 39 back

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);

        // Spend-allowance-first, so the refund unwinds in the same order: of the 39 back, 20 makes the pack
        // whole again and the remaining 19 is allowance once more. The sweep takes that 19 and nothing else,
        // leaving the pack at its full 50 plus a fresh allowance — the pack is never reached.
        Assert.Equal(50 + Allowance, await _ledger.GetBalanceCreditsAsync(id));
    }

    [Fact]
    public async Task A_reversal_of_last_months_charge_does_not_make_this_month_look_unspent()
    {
        // ⚠️ THE reason a Reversal row records which consumption it undoes. An act straddles the boundary:
        // charged in October, settles in November. Counted by where it LANDED, that refund makes November
        // look less spent than it was — so December's sweep measures a remainder that isn't there and takes
        // it out of the PACK. The household loses money it paid for, silently, and the clamp does not catch
        // it because the bogus remainder is smaller than the grant.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.GrantAsync(id, 500, "credit pack");
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);                          // +A1
        var oct = await _ledger.RecordConsumptionAsync(id, Allowance + 20, "a long plan"); // all of A1, then 20 of the pack

        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);                          // A1 spent out: sweep 0, +A2
        await _ledger.ReverseConsumptionAsync(id, 39, "refunded", oct!.Value);        // October's act settles
        await _ledger.RecordConsumptionAsync(id, Allowance, "November's own use");    // A2 spent in full

        await _ledger.EnsureCurrentAllowanceAsync(id, Dec);                          // must sweep NOTHING

        // 500 pack − 20 dipped + 39 back = 519, plus December's fresh allowance. Attributed to October,
        // the 39 belongs to an allowance already gone, so it rides in the persisting pool and November —
        // which really was spent to the last credit — sweeps zero.
        Assert.Equal(519 + Allowance, await _ledger.GetBalanceCreditsAsync(id));
    }

    [Fact]
    public async Task A_reversal_landing_after_the_month_rolls_over_does_not_inflate_the_new_month()
    {
        // ⚠️ An act can straddle the boundary: a 124-meal plan is eighteen provider calls, and the allowance
        // posts on any entitlement check in between. The Reversal then LANDS after the new allowance, so
        // read by its own position it looks like credit returned to a month that never paid it out. What
        // stops that is the attribution — the row records which consumption it undoes, and October's charge
        // is not November's — not the clamp, which this sequence does not reach. (The comment here used to
        // credit the clamp, and it was wrong: a bogus remainder smaller than the grant clears it untouched.
        // See A_reversal_of_last_months_charge_does_not_make_this_month_look_unspent.)
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.GrantAsync(id, 500, "credit pack");
        await _ledger.EnsureCurrentAllowanceAsync(id, Oct);           // +A1
        var charge = await _ledger.RecordConsumptionAsync(id, 42, "a long plan"); // charged against A1
        await _ledger.EnsureCurrentAllowanceAsync(id, Nov);           // rollover mid-act: sweep A1, grant +A2
        await _ledger.ReverseConsumptionAsync(id, 39, "refunded", charge!.Value); // the act settles after it

        await _ledger.EnsureCurrentAllowanceAsync(id, Dec);           // A2 was untouched, so sweep exactly A2

        // The pack is untouched, and the refunded 39 rode into December rather than being swept twice.
        Assert.Equal(500 + 39 + Allowance, await _ledger.GetBalanceCreditsAsync(id));
    }

    [Fact]
    public async Task A_reversal_is_refused_when_it_names_another_household_s_charge()
    {
        // ⚠️ auth.db has no household query filter, so every read and write hand-scopes — and this id is
        // not just stored, it is ACTED on: the unspent-allowance sum compares it against THIS household's
        // latest allowance. An id from somewhere else could make an allowance look unspent that isn't and
        // let the period-end sweep reach purchased credit.
        var mine = await SeedHouseholdAsync(HouseholdTier.Aware);
        var theirs = await SeedHouseholdAsync(HouseholdTier.Aware);
        var theirCharge = await _ledger.RecordConsumptionAsync(theirs, 10, "their chat turn");

        Assert.False(await _ledger.ReverseConsumptionAsync(mine, 10, "not mine to undo", theirCharge!.Value));

        Assert.Equal(0, await _ledger.GetBalanceCreditsAsync(mine));   // nothing minted
        Assert.Equal(-10, await _ledger.GetBalanceCreditsAsync(theirs)); // and nothing taken back from them
    }

    [Fact]
    public async Task A_reversal_is_refused_when_it_names_something_that_is_not_a_charge()
    {
        // A grant, an allowance, an expiry, or an id that names nothing at all. Reversing one would put
        // credits back against a row that never took any.
        var id = await SeedHouseholdAsync(HouseholdTier.Aware);
        await _ledger.GrantAsync(id, 500, "credit pack");
        await using var db = _authDb.CreateDbContext();
        var grant = await db.CreditLedger.Where(e => e.HouseholdId == id).Select(e => e.Id).SingleAsync();

        Assert.False(await _ledger.ReverseConsumptionAsync(id, 50, "undoing a grant?", grant));
        Assert.False(await _ledger.ReverseConsumptionAsync(id, 50, "undoing thin air?", 999_999));

        Assert.Equal(500, await _ledger.GetBalanceCreditsAsync(id));
    }
}
