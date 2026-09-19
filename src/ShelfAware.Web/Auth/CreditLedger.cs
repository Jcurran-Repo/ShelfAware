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
    /// <summary>The household's balance in CREDITS = the sum of its ledger entries (empty → 0).</summary>
    public async Task<long> GetBalanceCreditsAsync(string householdId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CreditLedger
            .Where(e => e.HouseholdId == householdId)
            .SumAsync(e => e.AmountCredits, cancellationToken);
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
            // LOWER Id. UnspentAllowanceCreditsAsync finds the latest allowance by max Id and nets Expiry rows
            // with a GREATER Id against it; if the Expiry landed after the new Allowance, next month it would
            // be netted against THAT allowance and the sweep would silently double-count (a wrong rollover).
            // Do not reorder these two Adds.
            var unspent = await UnspentAllowanceCreditsAsync(db, householdId, cancellationToken);
            if (unspent > 0)
                db.CreditLedger.Add(new CreditLedgerEntry
                {
                    HouseholdId = householdId,
                    Kind = CreditEntryKind.Expiry,
                    AmountCredits = -unspent,
                    Reason = "Monthly allowance expired (no rollover)",
                });

            var allowance = CreditPricing.MonthlyAllowanceCredits(billing.Value);
            if (allowance > 0)
                db.CreditLedger.Add(new CreditLedgerEntry
                {
                    HouseholdId = householdId,
                    Kind = CreditEntryKind.Allowance,
                    AmountCredits = allowance,
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

    /// <summary>The unspent remainder of the household's CURRENT (most recent) allowance, in credits: its
    /// amount, minus the consumption and any prior expiry since it was granted, plus any reversal that put
    /// credits back (spend-allowance-first, so all later consumption draws it down first). Zero when
    /// there's no prior allowance, or when it's already been exhausted.
    ///
    /// <para>⚠️ The Expiry term is what stops a re-sweep: if a previous period already swept this
    /// allowance (an Expiry row after it — which happens when the current month grants nothing, e.g.
    /// <c>MonthlyAllowanceDollars: 0</c>, so no NEWER Allowance becomes "the latest"), that Expiry nets the
    /// remainder to ≤ 0 and it is not swept again from persisting purchases.</para>
    ///
    /// <para>⚠️ The Reversal term is the same argument for credits coming BACK: an allowance credit that was
    /// charged and then refunded is unspent again within its own period, and leaving it out would let the
    /// household bank it past its month. That holds for a refund of a charge against the CURRENT allowance,
    /// which is what the term counts. A refund of an older period's charge is deliberately NOT counted —
    /// that month has closed, nothing sweeps it, and those credits do keep rolling. It is bounded, it errs
    /// toward the household, and the alternative takes purchased credit; see docs/subscription-plan.md
    /// §4.x for why it is held open rather than closed.</para></summary>
    private static async Task<long> UnspentAllowanceCreditsAsync(AuthDbContext db, string householdId, CancellationToken cancellationToken)
    {
        var lastAllowance = await db.CreditLedger
            .Where(e => e.HouseholdId == householdId && e.Kind == CreditEntryKind.Allowance)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.AmountCredits })
            .FirstOrDefaultAsync(cancellationToken);
        if (lastAllowance is null) return 0;

        // Consumption and Expiry are both stored NEGATIVE, so amount + (sum of those since) = amount drawn
        // down by spending AND by an expiry that already swept it — never re-sweeping the same allowance.
        var drawnSince = await db.CreditLedger
            .Where(e => e.HouseholdId == householdId
                && (e.Kind == CreditEntryKind.Consumption || e.Kind == CreditEntryKind.Expiry)
                && e.Id > lastAllowance.Id)
            .SumAsync(e => e.AmountCredits, cancellationToken);

        // ⚠️ Reversals are counted by WHICH CHARGE they undo, not by where they landed. A reversal is
        // positive where the two above are negative, so it nets a consumption back out — but only if that
        // consumption drew on THIS allowance. An act can straddle the boundary (a 124-meal plan is eighteen
        // provider calls, and the allowance posts on any entitlement check in between), and a refund of
        // last month's charge counted against this month's allowance makes this month look less spent than
        // it was: the sweep then takes the difference out of PURCHASED credit. Keying on ReversesEntryId
        // rather than on the reversal's own id is the whole point — the reversal's own position is exactly
        // the misleading fact. A reversal we cannot attribute (a legacy row, before the column existed) is
        // left out, which is the direction that can only under-sweep.
        var returnedSince = await db.CreditLedger
            .Where(e => e.HouseholdId == householdId
                && e.Kind == CreditEntryKind.Reversal
                && e.ReversesEntryId != null && e.ReversesEntryId > lastAllowance.Id)
            .SumAsync(e => e.AmountCredits, cancellationToken);

        var unspent = lastAllowance.AmountCredits + drawnSince + returnedSince;
        // Clamped at both ends. The lower bound is the original guard (a fully-spent allowance sweeps
        // nothing). The upper bound is belt-and-braces now that reversals are attributed: an allowance
        // cannot have more of itself left than was granted, whatever the rows say. ⚠️ It is NOT the fix for
        // the straddling reversal — the first version of this made that claim and it was false, because a
        // misattributed reversal SMALLER than the grant clears the clamp untouched and eats purchased
        // credit just the same. The attribution above is the fix; this only bounds a sum gone wrong some
        // other way. Math.Max guards the clamp itself: min > max throws, and a non-positive allowance row
        // could only arrive by hand-editing, which is not worth a crash inside the grant path.
        return Math.Clamp(unspent, 0, Math.Max(0, lastAllowance.AmountCredits));
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

    /// <summary>Append a CONSUMPTION entry (stored negative) — a charged action drawing the balance down.
    /// <paramref name="credits"/> is the positive price; a non-positive price (a FREE action per the price
    /// list, or a cached call that did no work) records nothing, which is what keeps the ledger a record of
    /// money rather than a log of everything that happened.
    ///
    /// <para>⚠️ It throws ONLY when the row provably did not land. The caller
    /// (<see cref="Services.MeteredChatClient"/>) hands the action's one charge back on a throw so a later
    /// round can pay instead, and a throw raised AFTER the INSERT committed — a connection reset on the
    /// context's dispose, say — would make that retry a SECOND ledger line for one act. The ledger is
    /// append-only with no idempotency key, so nothing would net them. This method is the only place that
    /// knows which side of the commit a failure came from, so it is where the distinction belongs.</para>
    /// </summary>
    /// <returns>The new row's id when one was written; null when the price was not chargeable. The id
    /// is what a later <see cref="ReverseConsumptionAsync"/> points at, so a refund can be attributed to
    /// the period the charge actually drew on rather than to whichever allowance precedes it.</returns>
    public async Task<long?> RecordConsumptionAsync(
        string householdId, long credits, string? reason, CancellationToken cancellationToken = default)
    {
        if (credits <= 0) return null;
        long? committed = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var entry = new CreditLedgerEntry
            {
                HouseholdId = householdId,
                Kind = CreditEntryKind.Consumption,
                AmountCredits = -credits,
                Reason = reason,
            };
            db.CreditLedger.Add(entry);
            await db.SaveChangesAsync(cancellationToken);
            // Read AFTER SaveChanges, which is where the store assigns it — and before anything that can
            // fail, so the swallow below still reports the row that landed rather than losing its id.
            committed = entry.Id;
        }
        catch (Exception ex) when (committed is not null)
        {
            // The money is recorded; what failed is the tidying after it (dispose, a connection reset).
            // Swallowed DELIBERATELY and not silently: reporting it as a failure would tell the caller to
            // retry the charge, which is the one outcome worse than the exception — the household would pay
            // twice for one action. Logged, because a connection that dies on dispose is worth knowing about.
            logger?.LogWarning(ex, "The consumption row for household {HouseholdId} was written, but the "
                + "context failed afterwards; reporting it as charged so the action is not billed twice.", householdId);
        }
        return committed;
    }

    /// <summary>Append a REVERSAL entry (positive) — credits handed back for an act that was charged and
    /// did not deliver. <paramref name="credits"/> is the positive amount coming back; non-positive records
    /// nothing.
    ///
    /// <para>⚠️ "Once per charge" is enforced in TWO places, on purpose. The scope that owns the charge
    /// runs this at most once — <see cref="Core.Billing.AiActionScope.DisposeAsync"/> takes the settlement
    /// callback with an <c>Interlocked.Exchange</c>, exactly as <c>TryClaimCharge</c> takes the charge —
    /// and that is the mechanism. The check below is the backstop, and it exists because the ledger is
    /// append-only with no way to net two rows afterwards: a second reversal is money minted and nothing
    /// downstream can undo it. <c>ReversesEntryId</c> is the key that makes the backstop possible at all,
    /// and it was added for the allowance attribution rather than for this — it earns its keep twice.</para>
    ///
    /// <para>The same commit-side distinction as <see cref="RecordConsumptionAsync"/>, for the same reason
    /// pointing the other way: a post-commit failure reported as a failure would invite a retry, and a
    /// second reversal would pay the household twice for one undelivered act.</para></summary>
    /// <returns>true when a row was written. false has two meanings and an operator needs both: there
    /// was nothing to give back (a non-positive amount), or the give-back was REFUSED by one of the guards
    /// below — a charge that isn't this household's, an amount larger than it drew, or one already
    /// reversed. Every refusal logs at Error; "nothing to give back" is silent.</returns>
    public async Task<bool> ReverseConsumptionAsync(
        string householdId, long credits, string? reason, long reversesEntryId,
        CancellationToken cancellationToken = default)
    {
        if (credits <= 0) return false;
        var committed = false;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            // ⚠️ The charge is checked before anything is written, because the unspent-allowance sum ACTS
            // on this id: one naming some other household's row, or a row that isn't a consumption, would
            // be compared against this household's latest allowance and could make an allowance look
            // unspent that isn't — letting the period-end sweep reach purchased credit. One caller passes
            // its own charge's id today; this is what stops the second caller getting it wrong silently.
            //
            // ⚠️ And there are three ways to get it wrong, not one. Naming the wrong row is the first;
            // giving back MORE than the charge and giving it back TWICE both mint credit, and neither can
            // be netted afterwards because the ledger is append-only with no idempotency key. The scope
            // enforces "once" for the caller that exists (AiActionScope.DisposeAsync takes the settlement
            // with an Interlocked.Exchange) and the metering layer bounds the amount — but both live in
            // the caller, and this is the layer that writes the money.
            //
            // ⚠️ The already-reversed check catches a REPEAT and two things it does not catch are worth
            // knowing. It is a read and then a write with no transaction around them and no unique index
            // on ReversesEntryId, so two concurrent reversals of one charge would both read "not yet" and
            // both insert — a race the scope's Interlocked take already prevents, which is why this stays
            // the cheap check rather than growing into a partial unique index. And a reversal written
            // before the column existed carries a null ReversesEntryId (see the unspent-allowance sum,
            // which leaves those out for the same reason), so a charge undone back then can be undone
            // again. Both are narrow; a guard sitting where this one sits will be read as total unless it
            // says otherwise.
            var charge = await db.CreditLedger
                .Where(e => e.Id == reversesEntryId
                    && e.HouseholdId == householdId
                    && e.Kind == CreditEntryKind.Consumption)
                .Select(e => (long?)e.AmountCredits)
                .FirstOrDefaultAsync(cancellationToken);
            if (charge is not { } drawn)
            {
                logger?.LogError("Refusing to reverse {Credits} credit(s) for household {HouseholdId}: "
                    + "entry {EntryId} is not a consumption of theirs.", credits, householdId, reversesEntryId);
                return false;
            }

            // A consumption is stored negative, so the charge's size is its magnitude.
            if (credits > -drawn)
            {
                logger?.LogError("Refusing to reverse {Credits} credit(s) for household {HouseholdId}: "
                    + "entry {EntryId} only drew {Drawn}.", credits, householdId, reversesEntryId, -drawn);
                return false;
            }

            if (await db.CreditLedger.AnyAsync(e => e.Kind == CreditEntryKind.Reversal
                    && e.ReversesEntryId == reversesEntryId
                    && e.HouseholdId == householdId, cancellationToken))
            {
                logger?.LogError("Refusing to reverse {Credits} credit(s) for household {HouseholdId}: "
                    + "entry {EntryId} has already been given back.", credits, householdId, reversesEntryId);
                return false;
            }

            db.CreditLedger.Add(new CreditLedgerEntry
            {
                HouseholdId = householdId,
                Kind = CreditEntryKind.Reversal,
                AmountCredits = credits,
                Reason = reason,
                ReversesEntryId = reversesEntryId,
            });
            await db.SaveChangesAsync(cancellationToken);
            committed = true;
        }
        catch (Exception ex) when (committed)
        {
            logger?.LogWarning(ex, "The reversal row for household {HouseholdId} was written, but the "
                + "context failed afterwards; reporting it as given back so the act is not refunded twice.", householdId);
        }
        return committed;
    }

    /// <summary>Append a GRANT entry (positive) — the welcome grant on a standalone context, or an admin
    /// comp later. A non-positive amount records nothing.</summary>
    public async Task GrantAsync(
        string householdId, long credits, string? reason, CancellationToken cancellationToken = default)
    {
        if (credits <= 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.CreditLedger.Add(new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Grant,
            AmountCredits = credits,
            Reason = reason,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The welcome-grant entry for a new household — a FACTORY, so a registration can add it to
    /// its OWN context (atomic with creating the household) while "what the welcome grant is" stays a
    /// single definition. Amount is the configured cost-dollars at the anchor (0 → no entry worth adding).</summary>
    public static CreditLedgerEntry? WelcomeGrant(string householdId, BillingOptions options)
    {
        var credits = CreditPricing.WelcomeGrantCredits(options);
        return credits <= 0 ? null : new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Grant,
            AmountCredits = credits,
            Reason = "Welcome grant",
        };
    }

    /// <summary>A credit-PACK purchase entry (positive credits) — a FACTORY so the webhook handler
    /// adds it to its own context, atomic with the tier/period write and the idempotency row, while the
    /// entry's shape stays defined here. Non-positive → null (nothing worth recording).</summary>
    public static CreditLedgerEntry? Purchase(string householdId, long credits, string? reason) =>
        credits <= 0 ? null : new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Purchase,
            AmountCredits = credits,
            Reason = reason,
        };

    /// <summary>A REFUND reversal entry (stored NEGATIVE) — a FACTORY, same batching reason as
    /// <see cref="Purchase"/>. <paramref name="credits"/> is the positive amount being reversed; the
    /// balance may go negative as a result (§4: a refund after credits were spent nets against future
    /// purchases). Non-positive → null.</summary>
    public static CreditLedgerEntry? Refund(string householdId, long credits, string? reason) =>
        credits <= 0 ? null : new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Refund,
            AmountCredits = -credits,
            Reason = reason,
        };
}
