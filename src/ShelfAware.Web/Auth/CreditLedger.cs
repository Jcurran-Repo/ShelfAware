using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;

namespace ShelfAware.Web.Auth;

/// <summary>
/// The credit ledger's read/write path (docs/subscription-plan.md §4 — the money record). Append-only:
/// a balance is the SUM of a household's entries, never a mutated running total, so there is no
/// read-modify-write race to lose. auth.db has no tenancy query filter, so every method hand-scopes its
/// WHERE to the household id (the ApiTokenService pattern).
///
/// ⚠️ The balance is read FRESH on every call, never cached for a scope — unlike phase 1's boolean tier,
/// a balance changes with each AI call, and a Blazor circuit can live for hours, so a per-scope cache
/// would let one long session overspend (the gate flag carried on IEntitlements). The balance is ENFORCED
/// by <see cref="Services.MeteredChatClient"/> (phase 4b) and shown in Settings.
/// </summary>
public sealed class CreditLedger(IDbContextFactory<AuthDbContext> dbFactory, IOptions<BillingOptions> billing, ILogger<CreditLedger>? logger = null)
{
    /// <summary>The household's balance in retail micros = the sum of its ledger entries (empty → 0).</summary>
    public async Task<long> GetBalanceMicrosAsync(string householdId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CreditLedger
            .Where(e => e.HouseholdId == householdId)
            .SumAsync(e => e.AmountMicros, cancellationToken);
    }

    /// <summary>The billing period an allowance belongs to: the first instant of <paramref name="now"/>'s
    /// CALENDAR MONTH, in UTC. ⚠️ Deliberately NOT the subscription's renewal date — an annual subscriber
    /// renews once a year, but the plan (§4) promises the grant still "drips monthly", and §4 names annual
    /// billing (one webhook per YEAR) as the exact trigger this lazy grant exists to work around. Keying on
    /// the calendar month makes the drip monthly regardless of billing cadence ("calendar month acceptable
    /// v1", §4). UTC so a server timezone change can't shift the boundary (the TZ gotcha).</summary>
    public static DateTimeOffset PeriodFor(DateTimeOffset now)
    {
        var u = now.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Lazily grant the CURRENT CALENDAR MONTH's Aware allowance, sweeping the previous month's unspent one
    /// (no rollover) — docs/subscription-plan.md §4. Called on the entitlement hot path (the balance is read
    /// right after), so it is idempotent within a month: once
    /// <see cref="Household.AllowanceGrantedForPeriod"/> equals this month's <see cref="PeriodFor"/> value,
    /// it does nothing (and never writes). <paramref name="now"/> defaults to <see cref="DateTimeOffset.UtcNow"/>
    /// (tests pass a fixed instant to exercise the month rollover).
    ///
    /// Only an <see cref="HouseholdTier.Aware"/> household gets an allowance (Tier is the active-subscription
    /// signal — the webhook drops it to Free on cancel; grant continuity rides that webhook, so a delayed
    /// cancel event keeps granting until it lands — bounded to ~one month's ~$1-cost allowance, accepted);
    /// the one-time welcome grant and purchased credits are
    /// separate pools that persist. Consumption spends the allowance FIRST, so the swept remainder is exactly
    /// the allowance's unspent part and the persisting balance is untouched. Concurrency-safe: the month is
    /// CLAIMED with a conditional <c>ExecuteUpdate</c> (the invite-code pattern) inside a transaction, so of
    /// two concurrent first-checks only the winner (rows == 1) posts the expiry + grant. auth.db has no query
    /// filter, so every statement hand-scopes to the household id.
    /// </summary>
    public async Task EnsureCurrentAllowanceAsync(
        string householdId, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var period = PeriodFor(now ?? DateTimeOffset.UtcNow);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Fast path (no write): only an Aware household that has rolled into a not-yet-granted month does
        // anything. ⚠️ Accepted edge: when a subscription is cancelled the tier drops to Free, so this returns
        // before sweeping — the final month's UNSPENT allowance is never expired and lingers as a spendable
        // balance until drawn down. Bounded to ≤ one allowance (~$1 cost), safe direction, and fair (they
        // paid for that month); deliberately not swept-on-cancel.
        var h = await db.Households.AsNoTracking()
            .Where(x => x.Id == householdId)
            .Select(x => new { x.Tier, x.AllowanceGrantedForPeriod })
            .FirstOrDefaultAsync(cancellationToken);
        if (h is null || h.Tier != HouseholdTier.Aware) return;
        if (h.AllowanceGrantedForPeriod == period) return;

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            // Claim the month atomically — only the writer whose UPDATE actually changes the marker
            // (rows == 1) posts entries, so two concurrent first-checks can't both grant. The WHERE
            // re-asserts tier + marker so a state change since the fast-path read can't be granted against.
            var claimed = await db.Households
                .Where(x => x.Id == householdId
                    && x.Tier == HouseholdTier.Aware
                    && x.AllowanceGrantedForPeriod != period)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowanceGrantedForPeriod, period), cancellationToken);
            if (claimed == 0) { await tx.RollbackAsync(cancellationToken); return; }

            // No rollover: sweep the prior allowance's unspent remainder BEFORE posting the new one.
            // ⚠️ ORDER IS LOAD-BEARING: the Expiry MUST be Added before the new Allowance below, so it gets a
            // LOWER Id. UnspentAllowanceMicrosAsync finds the latest allowance by max Id and nets Expiry rows
            // with a GREATER Id against it; if the Expiry landed after the new Allowance, next month it would
            // be netted against THAT allowance and the sweep would silently double-count (a wrong rollover).
            // Do not reorder these two Adds.
            var unspent = await UnspentAllowanceMicrosAsync(db, householdId, cancellationToken);
            if (unspent > 0)
                db.CreditLedger.Add(new CreditLedgerEntry
                {
                    HouseholdId = householdId,
                    Kind = CreditEntryKind.Expiry,
                    AmountMicros = -unspent,
                    Reason = "Monthly allowance expired (no rollover)",
                });

            var allowance = AiPricing.MonthlyAllowanceRetailMicros(billing.Value);
            if (allowance > 0)
                db.CreditLedger.Add(new CreditLedgerEntry
                {
                    HouseholdId = householdId,
                    Kind = CreditEntryKind.Allowance,
                    AmountMicros = allowance,
                    Reason = "Monthly allowance",
                });

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Best-effort: a lost write race or a transient auth.db error must NOT fail the entitlement check
            // that called this (that would wrongly block a paying subscriber). The marker isn't committed, so
            // the NEXT check re-attempts. Logged so a persistent failure is visible.
            logger?.LogWarning(ex, "Couldn't post the monthly allowance for household {HouseholdId}; the next entitlement check retries.", householdId);
        }
    }

    /// <summary>The unspent remainder of the household's CURRENT (most recent) allowance: its amount minus
    /// the consumption AND any prior expiry since it was granted (spend-allowance-first, so all later
    /// consumption draws it down first). Zero when there's no prior allowance, or when it's already been
    /// exhausted. ⚠️ The Expiry term is what stops a re-sweep: if a previous period already swept this
    /// allowance (an Expiry row after it — which happens when the current month grants nothing, e.g.
    /// <c>MonthlyAllowanceDollars: 0</c>, so no NEWER Allowance becomes "the latest"), that Expiry nets the
    /// remainder to ≤ 0 and it is not swept again from persisting purchases.</summary>
    private static async Task<long> UnspentAllowanceMicrosAsync(AuthDbContext db, string householdId, CancellationToken cancellationToken)
    {
        var lastAllowance = await db.CreditLedger
            .Where(e => e.HouseholdId == householdId && e.Kind == CreditEntryKind.Allowance)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.AmountMicros })
            .FirstOrDefaultAsync(cancellationToken);
        if (lastAllowance is null) return 0;

        // Consumption and Expiry are both stored NEGATIVE, so amount + (sum of those since) = amount drawn
        // down by spending AND by an expiry that already swept it — never re-sweeping the same allowance.
        var drawnSince = await db.CreditLedger
            .Where(e => e.HouseholdId == householdId
                && (e.Kind == CreditEntryKind.Consumption || e.Kind == CreditEntryKind.Expiry)
                && e.Id > lastAllowance.Id)
            .SumAsync(e => e.AmountMicros, cancellationToken);
        var unspent = lastAllowance.AmountMicros + drawnSince;
        return unspent > 0 ? unspent : 0;
    }

    /// <summary>A household's ledger entries, oldest first (by Id — SQLite can't ORDER BY a
    /// DateTimeOffset, and insert order IS chronological). For the data export: the ledger is the
    /// household's money record, so it's part of "download my data".</summary>
    public async Task<IReadOnlyList<CreditLedgerEntry>> ListForHouseholdAsync(
        string householdId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CreditLedger.AsNoTracking()
            .Where(e => e.HouseholdId == householdId)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Append a CONSUMPTION entry (stored negative) — an AI call drawing the balance down.
    /// <paramref name="retailMicros"/> is the positive retail amount; a non-positive amount (a free or
    /// cached call) records nothing.</summary>
    public async Task RecordConsumptionAsync(
        string householdId, long retailMicros, string? reason, CancellationToken cancellationToken = default)
    {
        if (retailMicros <= 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.CreditLedger.Add(new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Consumption,
            AmountMicros = -retailMicros,
            Reason = reason,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Append a GRANT entry (positive) — the welcome grant on a standalone context, or an admin
    /// comp later. A non-positive amount records nothing.</summary>
    public async Task GrantAsync(
        string householdId, long amountMicros, string? reason, CancellationToken cancellationToken = default)
    {
        if (amountMicros <= 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.CreditLedger.Add(new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Grant,
            AmountMicros = amountMicros,
            Reason = reason,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The welcome-grant entry for a new household — a FACTORY, so a registration can add it to
    /// its OWN context (atomic with creating the household) while "what the welcome grant is" stays a
    /// single definition. Amount is the configured cost-dollars × markup (0 → no entry worth adding).</summary>
    public static CreditLedgerEntry? WelcomeGrant(string householdId, BillingOptions options)
    {
        var amount = AiPricing.WelcomeGrantRetailMicros(options);
        return amount <= 0 ? null : new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Grant,
            AmountMicros = amount,
            Reason = "Welcome grant",
        };
    }

    /// <summary>A credit-PACK purchase entry (positive retail micros) — a FACTORY so the webhook handler
    /// adds it to its own context, atomic with the tier/period write and the idempotency row, while the
    /// entry's shape stays defined here. Non-positive → null (nothing worth recording).</summary>
    public static CreditLedgerEntry? Purchase(string householdId, long retailMicros, string? reason) =>
        retailMicros <= 0 ? null : new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Purchase,
            AmountMicros = retailMicros,
            Reason = reason,
        };

    /// <summary>A REFUND reversal entry (stored NEGATIVE) — a FACTORY, same batching reason as
    /// <see cref="Purchase"/>. <paramref name="retailMicros"/> is the positive amount being reversed; the
    /// balance may go negative as a result (§4: a refund after credits were spent nets against future
    /// purchases). Non-positive → null.</summary>
    public static CreditLedgerEntry? Refund(string householdId, long retailMicros, string? reason) =>
        retailMicros <= 0 ? null : new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Refund,
            AmountMicros = -retailMicros,
            Reason = reason,
        };
}
