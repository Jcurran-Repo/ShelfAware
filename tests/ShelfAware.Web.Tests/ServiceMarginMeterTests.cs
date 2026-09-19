using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Billing;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The reconciliation table: per action, what was charged against what it cost. This is how "is this
/// price right?" gets answered with evidence instead of a guess, so what matters is that it accumulates
/// honestly — a five-round chat turn is five calls and ONE charge, and nothing here is attributable to a
/// household. Real SQLite, because the upsert's race is a SQLite constraint.
/// </summary>
public class ServiceMarginMeterTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly TestAuthDb _authDb = new();
    private readonly ServiceMarginMeter _meter;

    public ServiceMarginMeterTests() => _meter = new ServiceMarginMeter(_authDb, NullLogger<ServiceMarginMeter>.Instance);

    public void Dispose() => _authDb.Dispose();

    [Fact]
    public async Task One_call_becomes_one_line_with_its_cost_and_its_charge()
    {
        await _meter.RecordAsync(ServiceAction.ReceiptExtraction, Today, costMicros: 4_200, creditsCharged: 1, billable: true);

        var line = Assert.Single(await _meter.ReadAsync(days: 1));
        Assert.Equal(ServiceAction.ReceiptExtraction, line.Action);
        Assert.Equal(1, line.Calls);
        Assert.Equal(1, line.Charges);
        Assert.Equal(1, line.CreditsCharged);
        Assert.Equal(4_200, line.CostMicros);
    }

    [Fact]
    public async Task Repeat_calls_add_onto_the_days_row_rather_than_stacking_rows()
    {
        await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 350, creditsCharged: 2, billable: true);
        await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 700, creditsCharged: 0, billable: true);
        await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 150, creditsCharged: 0, billable: true);

        await using var db = _authDb.CreateDbContext();
        Assert.Single(await db.ServiceMargin.AsNoTracking().ToListAsync()); // one row per day+action

        var line = Assert.Single(await _meter.ReadAsync(days: 1));
        // ⚠️ Three calls, ONE charge — exactly the shape a multi-round chat turn leaves. Reading margin off
        // Calls instead of Charges would make every tool-using turn look three times cheaper than it is.
        Assert.Equal(3, line.Calls);
        Assert.Equal(1, line.Charges);
        Assert.Equal(2, line.CreditsCharged);
        Assert.Equal(1_200, line.CostMicros);
    }

    [Fact]
    public async Task A_call_that_was_never_charged_still_records_its_cost()
    {
        // A Founder's call, a billing-off box, a free-priced action: all cost the host real money, and the
        // question this table answers is about the ACTION, not about who ran it. Charges stays 0 so margin
        // is read from what was actually billed.
        await _meter.RecordAsync(ServiceAction.TagSuggest, Today, costMicros: 90, creditsCharged: 0, billable: false);

        var line = Assert.Single(await _meter.ReadAsync(days: 1));
        Assert.Equal(1, line.Calls);
        Assert.Equal(0, line.Charges);
        Assert.Equal(0, line.CreditsCharged);
        Assert.Equal(90, line.CostMicros);
    }

    [Fact]
    public async Task Actions_are_kept_apart_and_listed_dearest_first()
    {
        await _meter.RecordAsync(ServiceAction.TagSuggest, Today, costMicros: 90, creditsCharged: 0, billable: false);
        await _meter.RecordAsync(ServiceAction.CensusPhoto, Today, costMicros: 9_000, creditsCharged: 1, billable: true);
        await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 1_000, creditsCharged: 2, billable: true);

        var lines = await _meter.ReadAsync(days: 1);

        // Dearest first: the operator's question is "what is eating the money?", so the answer leads.
        Assert.Equal(
            new ServiceAction?[] { ServiceAction.CensusPhoto, ServiceAction.ChatTurn, ServiceAction.TagSuggest },
            lines.Select(l => l.Action).ToArray());
    }

    [Fact]
    public async Task Several_unlabelled_rows_for_one_day_still_read_as_one_line()
    {
        // ⚠️ SQLite counts NULLs as DISTINCT, so the unique index can't merge unlabelled rows and a race
        // really can leave two for one day. The reader GROUPs rather than assuming one row per key — which
        // is what stops the admin page showing "Unlabelled" twice with the total split between them.
        await using (var db = _authDb.CreateDbContext())
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            db.ServiceMargin.Add(new ServiceMarginDay { Day = today, Action = null, Calls = 1, Charges = 1, CreditsCharged = 1, CostMicros = 100 });
            db.ServiceMargin.Add(new ServiceMarginDay { Day = today, Action = null, Calls = 2, Charges = 0, CreditsCharged = 0, CostMicros = 200 });
            await db.SaveChangesAsync();
        }

        var line = Assert.Single(await _meter.ReadAsync(days: 1));
        Assert.Null(line.Action);
        Assert.Equal("Unlabelled", line.Label);
        Assert.Equal(3, line.Calls);
        Assert.Equal(300, line.CostMicros);
    }

    [Fact]
    public async Task The_window_ends_at_the_days_asked_for()
    {
        await using (var db = _authDb.CreateDbContext())
        {
            db.ServiceMargin.Add(new ServiceMarginDay
            {
                Day = DateOnly.FromDateTime(DateTime.Today).AddDays(-30),
                Action = ServiceAction.ChatTurn, Calls = 9, Charges = 9, CreditsCharged = 18, CostMicros = 9_999,
            });
            await db.SaveChangesAsync();
        }
        await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 350, creditsCharged: 2, billable: true);

        Assert.Equal(350, Assert.Single(await _meter.ReadAsync(days: 7)).CostMicros);       // today only
        Assert.Equal(10_349, Assert.Single(await _meter.ReadAsync(days: 31)).CostMicros);   // both
    }

    [Fact]
    public async Task A_bookkeeping_failure_never_reaches_the_caller()
    {
        // It runs in the metering tail, AFTER the household already got their answer. A dead auth.db is a
        // missing reconciliation row — never a failed call.
        await using var db = _authDb.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(@"DROP TABLE ""ServiceMargin"";");

        await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 350, creditsCharged: 2, billable: true); // no throw
    }

    [Fact]
    public async Task Cost_per_charge_counts_only_what_a_billed_household_actually_cost()
    {
        // ⚠️ THE finding this column exists to answer. The operator is a Founder on every box here, so most
        // calls cost money and bill nobody. Recording one cost and dividing it by the billed count reported
        // a chat turn as costing eleven times what it does — on the one panel written to tell the operator
        // whether the price list is right.
        for (var i = 0; i < 10; i++)
            await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 1_000, creditsCharged: 0, billable: false);
        await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 1_000, creditsCharged: 2, billable: true);

        var line = Assert.Single(await _meter.ReadAsync(days: 1));
        Assert.Equal(11, line.Calls);
        Assert.Equal(1, line.Charges);
        Assert.Equal(11_000, line.CostMicros);          // every call — what the box actually spent
        Assert.Equal(1_000, line.BillableCostMicros);   // only the one somebody was on the hook for
        Assert.Equal(1_000, line.CostPerCharge);        // not 11_000
    }

    [Fact]
    public async Task The_silent_rounds_of_a_paid_action_count_toward_what_that_charge_cost()
    {
        // Billable is not "was charged": four of a five-round chat turn draw nothing, and their cost is
        // precisely what the one charge had to cover. Leaving them out would report the turn as costing a
        // fifth of what it does, which is the same defect pointing the other way.
        await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 500, creditsCharged: 2, billable: true);
        for (var i = 0; i < 4; i++)
            await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 500, creditsCharged: 0, billable: true);

        var line = Assert.Single(await _meter.ReadAsync(days: 1));
        Assert.Equal(2_500, line.BillableCostMicros);
        Assert.Equal(2_500, line.CostPerCharge);
    }

    [Fact]
    public async Task With_nothing_billed_there_is_no_cost_per_charge_to_report()
    {
        // A Founder-only box. The honest answer is "no evidence yet", not a number computed by dividing by
        // zero charges or by quietly substituting the call count.
        await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 1_000, creditsCharged: 0, billable: false);

        var line = Assert.Single(await _meter.ReadAsync(days: 1));
        Assert.Equal(1_000, line.CostMicros);
        Assert.Equal(0, line.BillableCostMicros);
        Assert.Null(line.CostPerCharge);
    }

    [Fact]
    public async Task Nothing_recorded_here_names_a_household()
    {
        // ⚠️ The reason this table can live in auth.db with no query filter and no tenancy drill: it is
        // box-wide operator data by construction. A household id arriving on it later would make it tenant
        // data that export and delete-my-data would both owe something to, silently.
        await _meter.RecordAsync(ServiceAction.ChatTurn, Today, costMicros: 350, creditsCharged: 2, billable: true);

        Assert.DoesNotContain(
            typeof(ServiceMarginDay).GetProperties(),
            p => p.Name.Contains("Household", StringComparison.OrdinalIgnoreCase));
    }

    // ---- reversals: the operator's row follows the ledger when an act is refunded ----

    [Fact]
    public async Task A_reversal_comes_off_the_day_the_charge_landed_not_the_day_it_settled()
    {
        // ⚠️ An act can run across midnight — a 124-meal plan is eighteen provider calls — so its charge
        // can sit on yesterday's row while the settlement runs today. Taking the correction off TODAY's row
        // corrects the wrong day against the right number and leaves both days permanently wrong, in
        // opposite directions. And there is usually a row there to hit: the same actions run every day.
        var meter = new ServiceMarginMeter(_authDb, NullLogger<ServiceMarginMeter>.Instance);
        var yesterday = Today.AddDays(-1);
        await meter.RecordAsync(ServiceAction.MealPlan, yesterday, 500, 42, billable: true);
        await meter.RecordAsync(ServiceAction.MealPlan, Today, 300, 5, billable: true);

        await meter.RecordReversalAsync(ServiceAction.MealPlan, yesterday, 39, wholeCharge: false);

        var rows = (await meter.ReadAsync(days: 3)).ToList();
        // ReadAsync groups by action, so assert the total: 42 + 5 − 39 = 8, which is only right if the 39
        // came off yesterday. Off today's row the floor would have clamped it to 0 and the total read 42.
        Assert.Equal(8, Assert.Single(rows).CreditsCharged);
    }

    [Fact]
    public async Task A_reversal_with_no_matching_charge_floors_at_zero_rather_than_going_negative()
    {
        // The row is a box-wide daily total that every household writes into, and RecordAsync is
        // best-effort — it gives up on a lost insert race — so a reversal can outlive its own charge.
        // CostPerCharge reads null at Charges <= 0, so a negative row makes the operator's "is this price
        // right?" answer silently DISAPPEAR for that action, which is the one surface that would have
        // shown any of this.
        var meter = new ServiceMarginMeter(_authDb, NullLogger<ServiceMarginMeter>.Instance);
        await meter.RecordAsync(ServiceAction.ChatTurn, Today, 400, 1, billable: true);

        await meter.RecordReversalAsync(ServiceAction.ChatTurn, Today, 5, wholeCharge: true);

        var line = Assert.Single(await meter.ReadAsync(days: 1));
        Assert.Equal(0, line.CreditsCharged);   // floored, not −4
        Assert.Equal(0, line.Charges);          // floored, not −1
        Assert.Equal(1, line.Calls);            // the call still happened and still cost money
    }

    [Fact]
    public async Task A_reversal_that_finds_no_row_at_all_changes_nothing()
    {
        // The charge's day has no row for this action — the act straddled midnight on a box where nothing
        // else ran that action. Nothing to correct, and nothing invented: RecordReversalAsync deliberately
        // has no insert-if-missing counterpart, because a row created here would claim a charge that never
        // reached this table. It says so in the log instead.
        var meter = new ServiceMarginMeter(_authDb, NullLogger<ServiceMarginMeter>.Instance);
        await meter.RecordAsync(ServiceAction.ChatTurn, Today, 400, 2, billable: true);

        await meter.RecordReversalAsync(ServiceAction.ChatTurn, Today.AddDays(-1), 2, wholeCharge: true);

        var line = Assert.Single(await meter.ReadAsync(days: 3));
        Assert.Equal(2, line.CreditsCharged);   // today's row is untouched
        Assert.Equal(1, line.Charges);
    }
}
