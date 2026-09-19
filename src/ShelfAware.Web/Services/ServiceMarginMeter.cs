using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Billing;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Services;

/// <summary>
/// Records, per day and per <see cref="ServiceAction"/>, what the app CHARGED in credits against what that
/// work COST in provider dollars — the reconciliation the credit unit exists to make possible
/// (docs/remediation-plan.md §7.2d). Box-wide operator data in auth.db (<see cref="ServiceMarginDay"/>), so
/// it crosses no tenancy boundary: nothing here is attributed to a household.
///
/// <para>Singleton, same shape as <see cref="DemoUsageMeter"/>: a race-safe in-place increment, falling back
/// to an insert only when the day/action has no row yet. Every write is BEST-EFFORT and called from the
/// metering tail — a bookkeeping hiccup must never fail a call that already succeeded.</para>
///
/// <para>⚠️ Recorded in EVERY key mode and at EVERY tier, unlike the credit ledger. A Founder's calls cost
/// the host real money and a BYOK visitor's don't, but both are work the operator wants priced — and the
/// point of this table is to answer "is this action's price right?", which is a question about the ACTION,
/// not about who happened to run it. The credits column is 0 for calls that were never charged, so margin is
/// read per action from what was actually billed.</para>
///
/// <para>⚠️ Which is exactly why the cost is recorded TWICE: once for every call
/// (<see cref="ServiceMarginDay.CostMicros"/>) and once for the calls a household was actually on the hook
/// for (<see cref="ServiceMarginDay.BillableCostMicros"/>). The price question is answered by the second
/// against <see cref="ServiceMarginDay.Charges"/>; mixing them divides an all-calls cost by a billed-only
/// count and overstates every action on a box whose main user is a Founder — which is every box here.</para>
/// </summary>
public sealed class ServiceMarginMeter(
    IDbContextFactory<AuthDbContext> dbFactory,
    ILogger<ServiceMarginMeter> logger)
{
    /// <summary>Attribute one completed call to <paramref name="action"/> (null = unlabelled). Pass the
    /// credits actually charged for it — 0 for every call that wasn't the one that paid, so a five-round chat
    /// turn records five calls and one charge — and whether the call was on a BILLABLE path at all, which is
    /// true for every round of a paying household's action and false for a Founder's or a BYOK visitor's.
    /// <paramref name="billable"/> is not "was charged": the four silent rounds of a paid chat turn are
    /// billable and charge nothing, and their cost is what that one charge really bought.</summary>
    public async Task RecordAsync(ServiceAction? action, long costMicros, long creditsCharged, bool billable, CancellationToken ct = default)
    {
        var day = DateOnly.FromDateTime(DateTime.Today);
        var charges = creditsCharged > 0 ? 1 : 0;
        var billableCost = billable ? costMicros : 0;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await IncrementAsync(db, day, action, costMicros, billableCost, creditsCharged, charges, ct) > 0) return;

            db.ServiceMargin.Add(new ServiceMarginDay
            {
                Day = day,
                Action = action,
                Calls = 1,
                Charges = charges,
                CreditsCharged = creditsCharged,
                CostMicros = costMicros,
                BillableCostMicros = billableCost,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException
                { SqliteExtendedErrorCode: 2067 or 1555 }) // SQLITE_CONSTRAINT_UNIQUE / _PRIMARYKEY only
            {
                // A concurrent insert won the race; add onto their row (the DemoUsageMeter pattern).
                db.ChangeTracker.Clear();
                if (await IncrementAsync(db, day, action, costMicros, billableCost, creditsCharged, charges, ct) == 0)
                    logger.LogWarning("Service-margin upsert lost both the insert and the retry increment for {Day}/{Action}.", day, action);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording the service-margin row for {Action} failed; that call is missing from reconciliation.", action);
        }
    }

    /// <summary>The last <paramref name="days"/> days of reconciliation, one line per action, most-spent
    /// first. GROUPed rather than read row-for-row, so the several unlabelled rows a race can leave for one
    /// day (SQLite counts NULLs as distinct, so the unique index doesn't merge them) still read as one line.
    ///
    /// <para>No admin re-assert here, unlike <see cref="Data.AdminReportReader"/> and
    /// <see cref="Data.AdminAiSpendReader"/> — and the difference is the data, not an oversight. Those two
    /// carry production <c>IgnoreQueryFilters</c> over PANTRY tables, so a slip returns one household's rows
    /// to another; this table holds no household id and nothing derivable from one, so the worst a slip
    /// leaks is a box-wide count. That puts it with <see cref="DemoUsageMeter.GetTodayAsync"/>, which is also
    /// unguarded here and gated at the page (<c>[Authorize(AdminOptions.PolicyName)]</c> plus the
    /// <c>IsAdmin</c> re-check in <c>Admin.razor</c>'s initializer, which returns before this is reached).
    /// This is also a singleton, written from the metering tail where there is no user to ask.</para></summary>
    public async Task<IReadOnlyList<ServiceMarginLine>> ReadAsync(int days, CancellationToken ct = default)
    {
        var from = DateOnly.FromDateTime(DateTime.Today).AddDays(-Math.Max(0, days - 1));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lines = await db.ServiceMargin.AsNoTracking()
            .Where(d => d.Day >= from)
            .GroupBy(d => d.Action)
            .Select(g => new ServiceMarginLine(
                g.Key,
                g.Sum(d => d.Calls),
                g.Sum(d => d.Charges),
                g.Sum(d => d.CreditsCharged),
                g.Sum(d => d.CostMicros),
                g.Sum(d => d.BillableCostMicros)))
            .ToListAsync(ct);
        return [.. lines.OrderByDescending(l => l.CostMicros)];
    }

    private static Task<int> IncrementAsync(
        AuthDbContext db, DateOnly day, ServiceAction? action, long costMicros, long billableCost, long credits, int charges, CancellationToken ct)
        => db.ServiceMargin.Where(d => d.Day == day && d.Action == action)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Calls, d => d.Calls + 1)
                .SetProperty(d => d.Charges, d => d.Charges + charges)
                .SetProperty(d => d.CreditsCharged, d => d.CreditsCharged + credits)
                .SetProperty(d => d.CostMicros, d => d.CostMicros + costMicros)
                .SetProperty(d => d.BillableCostMicros, d => d.BillableCostMicros + billableCost), ct);
}

/// <summary>One action's reconciliation over a window: what it was charged against what it cost.</summary>
public sealed record ServiceMarginLine(
    ServiceAction? Action, int Calls, int Charges, long CreditsCharged, long CostMicros, long BillableCostMicros)
{
    /// <summary>What ONE billed action of this kind cost — the only honest comparison against its price, and
    /// null when nothing was billed (a Founder-only box has no charges to divide by). Reads the BILLABLE
    /// cost, not the total: see <see cref="ServiceMarginDay.BillableCostMicros"/>.</summary>
    public long? CostPerCharge => Charges > 0 ? BillableCostMicros / Charges : null;

    /// <summary>The human name for the row — the SAME wording the price list and a customer's ledger use
    /// (<see cref="CreditPricing.Describe"/>), so an operator comparing the two is reading one vocabulary.</summary>
    public string Label => Action is { } a ? CreditPricing.Describe(a) : "Unlabelled";
}
