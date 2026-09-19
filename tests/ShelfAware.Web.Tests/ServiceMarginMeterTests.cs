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
    private readonly TestAuthDb _authDb = new();
    private readonly ServiceMarginMeter _meter;

    public ServiceMarginMeterTests() => _meter = new ServiceMarginMeter(_authDb, NullLogger<ServiceMarginMeter>.Instance);

    public void Dispose() => _authDb.Dispose();

    [Fact]
    public async Task One_call_becomes_one_line_with_its_cost_and_its_charge()
    {
        await _meter.RecordAsync(ServiceAction.ReceiptExtraction, costMicros: 4_200, creditsCharged: 1);

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
        await _meter.RecordAsync(ServiceAction.ChatTurn, costMicros: 350, creditsCharged: 2);
        await _meter.RecordAsync(ServiceAction.ChatTurn, costMicros: 700, creditsCharged: 0);
        await _meter.RecordAsync(ServiceAction.ChatTurn, costMicros: 150, creditsCharged: 0);

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
        await _meter.RecordAsync(ServiceAction.TagSuggest, costMicros: 90, creditsCharged: 0);

        var line = Assert.Single(await _meter.ReadAsync(days: 1));
        Assert.Equal(1, line.Calls);
        Assert.Equal(0, line.Charges);
        Assert.Equal(0, line.CreditsCharged);
        Assert.Equal(90, line.CostMicros);
    }

    [Fact]
    public async Task Actions_are_kept_apart_and_listed_dearest_first()
    {
        await _meter.RecordAsync(ServiceAction.TagSuggest, costMicros: 90, creditsCharged: 0);
        await _meter.RecordAsync(ServiceAction.CensusPhoto, costMicros: 9_000, creditsCharged: 1);
        await _meter.RecordAsync(ServiceAction.ChatTurn, costMicros: 1_000, creditsCharged: 2);

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
        await _meter.RecordAsync(ServiceAction.ChatTurn, costMicros: 350, creditsCharged: 2);

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

        await _meter.RecordAsync(ServiceAction.ChatTurn, costMicros: 350, creditsCharged: 2); // no throw
    }

    [Fact]
    public async Task Nothing_recorded_here_names_a_household()
    {
        // ⚠️ The reason this table can live in auth.db with no query filter and no tenancy drill: it is
        // box-wide operator data by construction. A household id arriving on it later would make it tenant
        // data that export and delete-my-data would both owe something to, silently.
        await _meter.RecordAsync(ServiceAction.ChatTurn, costMicros: 350, creditsCharged: 2);

        Assert.DoesNotContain(
            typeof(ServiceMarginDay).GetProperties(),
            p => p.Name.Contains("Household", StringComparison.OrdinalIgnoreCase));
    }
}
