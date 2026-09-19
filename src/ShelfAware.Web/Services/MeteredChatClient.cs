using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Billing;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>
/// The metering skin over <see cref="ByokChatClient"/>. RECORDING of token USAGE is universal — every
/// call's tokens land in the household's usage row so the user can see what they've spent (the Settings
/// usage panel, the accuracy check's cost line). LIMITING (and credit-ledger drawdown) is managed-AND-billing
/// only: quotas guard the HOST's wallet, so BYOK circuits (their key, their wallet) and managed boxes with
/// no Payments config (unlimited by default — §7) are recorded but never blocked or charged. Sits at the top
/// of the IChatClient chain so every AI service (chat, extraction, advisors) is covered without touching them.
/// </summary>
public sealed class MeteredChatClient(
    ByokChatClient inner,
    CircuitAiSettings settings,
    AiUsageMeter meter,
    DemoUsageMeter demoMeter,
    IOptions<BillingOptions> billing,
    IOptions<PaymentsOptions> payments,
    CreditLedger ledger,
    ServiceMarginMeter margin,
    IEntitlements entitlements,
    ICurrentHousehold currentHousehold,
    ILogger<MeteredChatClient> logger) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await EnsureManagedCallAllowedAsync(cancellationToken);
        var reserved = await ReserveCallAsync();
        ChatResponse? response = null;
        try
        {
            response = await inner.GetResponseAsync(messages, options, cancellationToken);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A non-cancellation throw with no response — give the reserved call back so an outage doesn't
            // burn the caps on requests that never ran. This correctly releases the common outage refusals
            // (429/5xx/connection-refused/keyless). It keys on "not a cancellation" rather than "provably no
            // cost" because this decorator wraps a GENERIC IChatClient and can't read provider-specific
            // exception types: a rare POST-billing failure (e.g. the connection dropping while a completed
            // answer's body is read) is also released, under-counting the abuse cap by one. That is benign and
            // non-exploitable — a client can't provoke that shape (a client abort is OperationCanceledException
            // and stays counted), and a failed call is never charged (the finally below records nothing, since
            // `response` is null on every throw here).
            await ReleaseCallAsync(reserved);
            throw;
        }
        finally
        {
            // Record tokens/cost/credit UNCANCELLABLY once a response exists — a client that drops AFTER the
            // answer landed can't dodge the token/cost/credit write. A mid-flight abort (no response) records
            // nothing here; the CALL already counted at the reserve above, which is what bounds the caps.
            // Prefer the model the provider REPORTED, then the one we REQUESTED before AiPricing's own
            // priciest-tier fallback — so a provider that doesn't echo the model still prices at the real
            // (usually cheaper) requested model, not Opus rates.
            if (response is not null)
                await RecordUsageAsync(response.Usage, response.ModelId ?? options?.ModelId);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureManagedCallAllowedAsync(cancellationToken);
        var reserved = await ReserveCallAsync();

        // Create the enumerator in its OWN try/catch (no yield here) so an EAGER throw releases the reserved
        // call, matching GetResponseAsync: ByokChatClient builds the real client at this call and throws
        // InvalidOperationException on a blank key BEFORE any request, and that must be a release, not a
        // stuck reservation. (The SDK's own request stays lazy until the first MoveNextAsync below.)
        IAsyncEnumerator<ChatResponseUpdate> enumerator;
        try
        {
            enumerator = inner.GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReleaseCallAsync(reserved); // eager refusal, no output produced — release
            throw;
        }

        UsageDetails? usage = null;
        string? model = null;
        var yieldedAny = false;
        // Manual enumerator so the provider refusal (surfaced by MoveNextAsync) can be caught to release the
        // reserved call — `yield return` may not sit inside a try WITH a catch, so the catch guards only the
        // advance and the yield stays in the outer try/finally.
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync()) break;
                    update = enumerator.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A stream that breaks BEFORE producing any output is a refusal we release (see
                    // GetResponseAsync). Once ANY update has been received the provider has done billable work,
                    // so a later break STAYS counted — that is the streaming half of "release only before any
                    // cost", and it keeps "refused before output" distinct from "broke after a partial answer"
                    // (an abort/timeout is OperationCanceledException and stays counted regardless).
                    if (!yieldedAny) await ReleaseCallAsync(reserved);
                    throw;
                }
                yieldedAny = true; // an update arrived from the provider — it engaged, so it counts from here
                // Providers report usage in a trailing UsageContent update; remember the last one seen, and
                // the model id from whichever update carries it (for the cost lookup).
                foreach (var content in update.Contents)
                {
                    if (content is UsageContent u) usage = u.Details;
                }
                if (update.ModelId is not null) model = update.ModelId;
                yield return update;
            }
        }
        finally
        {
            // Record FIRST (uncancellable, see GetResponseAsync), so a throwing DisposeAsync can't skip the
            // tokens/cost/credit write — the record is the important half. Runs on normal completion AND when
            // the consumer stops early. Then dispose the enumerator.
            if (usage is not null)
                await RecordUsageAsync(usage, model ?? options?.ModelId);
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>The managed-call gate, consulted BEFORE the provider call (phase 4b) — the CHECKS only; the
    /// reserve is <see cref="ReserveCallAsync"/>, next. BYOK circuits skip it entirely (their key, their
    /// wallet). For a managed household: the per-household caps, then the demo box-wide valve, then
    /// <see cref="IEntitlements.CheckAiAsync"/> — always true where billing is off (§7), and otherwise a
    /// Founder (unlimited) or a balance that covers THIS ACT'S price. Throws to refuse — the provider call
    /// never happens, and (because the reserve runs after) nothing is counted.
    ///
    /// <para>⚠️ Two things this asks that a bare "any credit left?" did not, and a meal plan needs both.
    /// It asks for the price of the act about to run, so a household holding 5 credits is refused a
    /// 42-credit plan BEFORE a single batch is generated rather than after the first one has drained them.
    /// And it does not ask at all once the act's charge is already claimed: a plan charges its whole price
    /// on the first of eighteen calls, so re-asking on call two would refuse the rest of a plan the
    /// household has paid for in full — the plan would persist seven meals of the hundred and twenty-four
    /// it bought, and the page would report success.</para></summary>
    private async Task EnsureManagedCallAllowedAsync(CancellationToken cancellationToken)
    {
        if (!settings.Managed) return;
        await meter.EnsureLlmCallAllowedAsync(cancellationToken);
        // The demo box's BOX-WIDE daily valve (a no-op unless a Demo cap is configured) — the wallet bound
        // the per-household cap above can't give under open registration. Throws the come-back message.
        await demoMeter.EnsureCallAllowedAsync(cancellationToken);

        var act = AiActionScope.Current;
        if (act is { ChargeClaimed: true }) return; // bought and paid for; the rest of it is not a new spend
        // The SAME question every surface pre-check asks (AiErrorText.BlockedReasonAsync), asked of the same
        // method with the same act, so the two can never answer it differently. An unlabelled call passes a
        // null act and asks for the floor: it has no price until its cost is known (CreditsForCostMicros).
        if (!(await entitlements.CheckAiAsync(act?.Action, act?.Units ?? 1, cancellationToken)).Allowed)
            throw new AiCreditsExhaustedException();
    }

    /// <summary>Which of the two reserves DID NOT throw, so the matching release gives back ONLY those. A
    /// reserve write is best-effort (below) — if it silently fails, "releasing" it anyway would subtract a
    /// count that was never added and drive the counter below the true value (a negative row that raises the
    /// effective cap). Balanced reserve/release makes that impossible. (A no-op reserve — the box-wide valve
    /// on an unconfigured box — reads true and is released by an equally-no-op release, which is harmless.)</summary>
    private readonly record struct CallReservation(bool Household, bool BoxWide);

    /// <summary>Count one call, BEFORE the provider call and UNCANCELLABLY, so a client that aborts
    /// mid-flight still counts against the caps that bound volume — an aborted call still cost the key. The
    /// per-household count runs for BOTH modes (a BYOK visitor's own usage is recorded-but-never-limited,
    /// like their tokens); the box-wide demo valve counts host-key (managed) calls only. Best-effort like
    /// every usage write — a rare bookkeeping hiccup mustn't block a legitimate call, and the key's own spend
    /// limit is the hard backstop. Returns which reserves succeeded so <see cref="ReleaseCallAsync"/> can undo
    /// exactly those. Tokens/cost/credit can't be reserved here (they need the response); they record at the
    /// tail (<see cref="RecordUsageAsync"/>).</summary>
    private async Task<CallReservation> ReserveCallAsync()
    {
        var household = false;
        try { await meter.ReserveLlmCallAsync(CancellationToken.None); household = true; }
        catch (Exception ex) { logger.LogError(ex, "Reserving the AI call for the household usage row failed; it went uncounted."); }

        var boxWide = false;
        if (settings.Managed)
        {
            try { await demoMeter.RecordCallAsync(CancellationToken.None); boxWide = true; }
            catch (Exception ex) { logger.LogError(ex, "Reserving the demo box-wide call failed; it went uncounted for the daily valve."); }
        }
        return new CallReservation(household, boxWide);
    }

    /// <summary>Give the reserved call back, UNCANCELLABLY, when the provider REFUSED before any cost (a
    /// 429/5xx/connection error, or a keyless boot) — the mirror of <see cref="ReserveCallAsync"/>, so a
    /// provider outage doesn't burn the caps on requests that never ran. Called ONLY on a non-cancellation
    /// throw: an abort/timeout (<see cref="OperationCanceledException"/>) reached the provider and cost the
    /// key, so it stays counted. Releases ONLY the reserves that actually landed (<paramref name="reserved"/>),
    /// so a reserve that silently failed is never subtracted — no negative drift. Best-effort itself; a failed
    /// release just leaves the call counted (safe direction — the caps stay conservative).</summary>
    private async Task ReleaseCallAsync(CallReservation reserved)
    {
        if (reserved.Household)
        {
            try { await meter.ReleaseLlmCallAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogError(ex, "Releasing the reserved AI call failed; it stays counted against the household row."); }
        }

        if (reserved.BoxWide)
        {
            try { await demoMeter.ReleaseCallAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogError(ex, "Releasing the reserved demo box-wide call failed; it stays counted against the daily valve."); }
        }
    }

    /// <summary>Record a completed call's tokens + cost + credit draw, UNCANCELLABLY (the whole point of the
    /// finally that calls this): the caller's token may already be cancelled by the time we get here, and a
    /// bookkeeping write may not be skipped by that (items 27/39 — a write may not be cancelled). The CALL
    /// COUNT is not written here — it was reserved at the gate. The two writes are INDEPENDENT best-effort on
    /// two SEPARATE databases (the AiUsage row in the pantry, the credit ledger in auth), which no single
    /// transaction can span; each is guarded on its OWN so a pantry hiccup can't silently skip the MONEY
    /// write. A write that fails after its sibling landed is logged and bounded to this one call.</summary>
    private async Task RecordUsageAsync(UsageDetails? usage, string? model)
    {
        var inputTokens = usage?.InputTokenCount ?? 0;
        var outputTokens = usage?.OutputTokenCount ?? 0;

        long costMicros;
        try
        {
            // Stamp the cost at CALL time from the configured rate, so a later rate change never rewrites
            // this call's cost (docs §4). An unreported model falls back (never free) — see AiPricing.
            costMicros = AiPricing.CostMicros(billing.Value, model, inputTokens, outputTokens);
        }
        catch (Exception ex) // pure math — only an absurd token count could overflow the long cast
        {
            logger.LogError(ex, "Pricing this AI call failed; it went unrecorded.");
            return;
        }

        // ⚠️ The three writes below each swallow EVERYTHING, cancellation included, which is the opposite of
        // the house rule and deliberate here. This whole tail runs from a `finally` after the household
        // already has its answer, and every call passes CancellationToken.None — there is no cancellation to
        // honour, and an exception escaping a `finally` would replace a delivered answer with a crash. The
        // rule is "let cancellation propagate so work can stop"; there is no work left to stop.
        try
        {
            await meter.RecordLlmUsageAsync(inputTokens, outputTokens, costMicros, CancellationToken.None);
        }
        catch (Exception ex) { logger.LogError(ex, "Recording AI usage failed; this call's tokens/cost went unrecorded."); }

        var consumption = CreditConsumption.None;
        // ⚠️ ONE read of the date, shared by the charge's stamp and the margin row it lands on. Reading
        // it twice — once here and once inside the meter — reproduces the very defect the reversal's day
        // stamp was added to fix: straddle midnight between the two statements and the charge is recorded
        // on one day while its reversal aims at the other, leaving both permanently wrong. The window is
        // milliseconds rather than minutes, and it is the same one-fact-two-derivations shape either way.
        var today = DateOnly.FromDateTime(DateTime.Today);
        try
        {
            consumption = await RecordCreditConsumptionAsync(costMicros, model, today, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // ⚠️ Billable, drew nothing — NOT `None`. The throw can only come from past every "is this
            // household on the hook?" gate, so the call WAS billable and its cost is part of what the
            // action's charge has to cover. Recording it as unbillable would quietly leave it out of
            // /admin's cost-per-charge and flatter the margin on exactly the calls where money went wrong.
            consumption = CreditConsumption.Free;
            logger.LogError(ex, "Recording credit consumption failed; this call didn't draw the balance.");
        }

        // Reconciliation, box-wide and household-free: what this call COST against what it was CHARGED.
        // Recorded in every key mode and at every tier (see ServiceMarginMeter) — the question it answers is
        // "is this action's price right?", which is about the action, not about who ran it. Its own
        // best-effort INSIDE the meter, and wrapped here too: this runs from a finally (the streaming tail),
        // and an escaping exception there would destroy an answer the household has already been charged for.
        try
        {
            await margin.RecordAsync(
                AiActionScope.Current?.Action, today, costMicros, consumption.Credits, consumption.Billable, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording the service-margin row failed; this call is missing from reconciliation.");
        }
    }

    /// <summary>Draw the household's credit balance down by the PRICE OF THE ACTION this call belongs to —
    /// but only for a household that actually spends host credits: a MANAGED deployment with BILLING enabled
    /// (BYOK visitors ride their own key; a managed box with no <c>Payments</c> config is unlimited-by-default
    /// per §7, so the credit system doesn't apply and nothing is drawn) and a NON-unlimited tier (a Founder's
    /// cost is recorded above for the operator, but they never spend credit). ⚠️ The billing-off skip mirrors
    /// <see cref="IEntitlements.IsAiAllowedAsync"/>'s <c>!IsConfigured</c> short-circuit — the credit system
    /// is on or off as ONE thing (gate, pre-check, display, AND this recorder), so a billing-off box never
    /// accrues an invisible negative balance that flipping billing on would later enforce. This RECORDS
    /// consumption; the balance ENFORCEMENT is <see cref="EnsureManagedCallAllowedAsync"/> (phase 4b), which
    /// runs BEFORE the call — so this post-call hot path reads no balance.
    ///
    /// <para>⚠️ ONE charge per <see cref="AiActionScope"/>, claimed atomically, so a chat turn that needs five
    /// tool rounds costs a chat turn rather than five of them. The variance between the price and what the
    /// rounds actually cost is Jordan's — and in exchange the UI can state a price BEFORE the button is
    /// pressed, which a cost-denominated balance can never do.</para>
    ///
    /// <para>⚠️ A call with no scope (an AI service nobody has labelled) is NOT free: it falls back to the
    /// old cost-denominated charge via <see cref="CreditPricing.CreditsForCostMicros"/>, so a missing label
    /// over-charges visibly instead of opening a hole. The tier and household reads happen BEFORE the claim,
    /// so a call that was never going to be charged doesn't spend its scope's one charge.</para></summary>
    /// <returns>Whether this call was on a billable path at all, and the credits it actually drew — 0 for a
    /// free action and for every later round of an action already paid for. Both go to the reconciliation
    /// row: the credits say what was billed, the billable flag says whose cost that billing has to cover.
    /// </returns>
    private async Task<CreditConsumption> RecordCreditConsumptionAsync(
        long costMicros, string? model, DateOnly today, CancellationToken cancellationToken)
    {
        if (!settings.Managed || !payments.Value.IsConfigured) return CreditConsumption.None;
        if ((await entitlements.GetTierAsync(cancellationToken)).IsUnlimited()) return CreditConsumption.None;

        var householdId = await currentHousehold.GetIdAsync(cancellationToken);
        if (householdId is null) return CreditConsumption.None;

        // Past this point the call is BILLABLE whatever happens next: this household spends credit, and its
        // cost is what the action's price has to cover. The four silent rounds of a paid chat turn are each
        // billable and each charge nothing.
        long credits;
        string? reason;
        var claimed = AiActionScope.Current;
        if (claimed is not null)
        {
            if (!claimed.TryClaimCharge()) return CreditConsumption.Free; // a later round of an action already paid for
            credits = CreditPricing.CreditsFor(billing.Value, claimed.Action, claimed.Units);
            reason = CreditPricing.DescribeCharge(billing.Value, claimed.Action, claimed.Units);
        }
        else
        {
            credits = CreditPricing.CreditsForCostMicros(billing.Value, costMicros);
            reason = model;
        }

        long? chargeId;
        try
        {
            chargeId = await ledger.RecordConsumptionAsync(householdId, credits, reason, cancellationToken);
        }
        catch
        {
            // ⚠️ Hand the claim back before rethrowing. The claim is taken BEFORE the write (that is what
            // keeps two parallel rounds from both charging), so a write that fails having spent the claim
            // would make every REMAINING round of the action free too — one failed row losing the whole
            // action's charge. Releasing it lets the next round of the same action pay instead.
            // Safe to do unconditionally ONLY because RecordConsumptionAsync throws exclusively when the row
            // provably did not land; a failure after the INSERT committed is absorbed there and returns
            // normally. Releasing on a landed write would bill one action twice, which is the one outcome
            // worse than not billing it at all — see that method's remarks.
            claimed?.ReleaseCharge();
            throw;
        }

        // ⚠️ OUTSIDE that try, and it must stay outside. The row has landed by here, and the catch above
        // releases the claim unconditionally — which is correct only while RecordConsumptionAsync is the
        // only thing that can throw in there, because it throws exclusively when the row did NOT land.
        // ChargeRecorded can throw (a charge arriving against a closed scope), and inside the try that
        // throw would release a claim over money already taken: the next call on that flow would claim
        // again and bill the household a SECOND time for one act. Which is the one outcome the catch's own
        // remarks name as worse than not billing at all. Its failure is logged and swallowed instead: the
        // charge stands and the refund is unreachable, which is recoverable from the ledger, where losing
        // the claim is not.
        try
        {
            // Handed over only once the row has LANDED, so an act that was never charged — a Founder, a
            // BYOK circuit, a box with billing off, a free price — settles to nothing rather than being
            // paid credits it never spent.
            // ⚠️ Everything the give-back needs is STAMPED here, at charge time, and closed over: the row
            // it undoes, the amount, and the price the charge was computed at. Re-reading any of them when
            // the act closes would let a config edit mid-act price the refund differently from the charge —
            // the same rule RecordUsageAsync states for cost, applied to the correction.
            // ⚠️ The null-id case is logged rather than passed over. It cannot happen today — the ledger
            // returns null only for a non-positive price, which `credits > 0` has already excluded — but
            // the consequence if it ever does is a charge with the refund permanently unreachable and
            // nothing said, which is the shape this whole arc keeps finding.
            if (claimed is not null && credits > 0 && chargeId is null)
                logger.LogError("A charge of {Credits} credit(s) for household {HouseholdId} landed without "
                    + "a row id, so it cannot be given back.", credits, householdId);
            if (claimed is not null && credits > 0 && chargeId is { } id)
            {
                var pricedAt = billing.Value;
                claimed.ChargeRecorded(credits, (delivered, ct) =>
                    ReverseUndeliveredAsync(householdId, claimed, credits, id, pricedAt, today, delivered, ct));
            }
        }
        catch (Exception ex)
        {
            // Not narrowed to InvalidOperationException: anything thrown here is past the landed row, and
            // letting it escape would reach RecordUsageAsync's catch, which reports the call as having
            // drawn nothing — a statement the ledger contradicts.
            logger.LogError(ex, "A charge of {Credits} credit(s) for household {HouseholdId} on ledger row "
                + "{ChargeId} for {Action} landed but could not be made refundable; it will not be given "
                + "back if the act under-delivers.", credits, householdId, chargeId, claimed?.Action);
        }
        return new CreditConsumption(true, credits);
    }

    /// <summary>Give back the part of an act's charge that covered units it never delivered — the whole
    /// charge when it delivered nothing at all. Priced from the SAME <see cref="CreditPricing.CreditsFor"/>
    /// the charge read, over the units that actually arrived, so the household is left holding exactly what
    /// it would have been charged had the act asked for what it got.
    ///
    /// <para>⚠️ Failures here are logged and swallowed, and that is a deliberate exception to this repo's
    /// rule against swallowing. This runs on the act's own path, after the household already has its
    /// answer: a dead auth.db must not turn a delivered act into an error the surface reports as a
    /// failure — which would be the second time we told the household something went wrong about work that
    /// went right. What it costs is a household left charged for an undelivered unit, logged at Error and
    /// visible in the ledger, which is recoverable; the alternative is not.</para></summary>
    private async Task ReverseUndeliveredAsync(
        string householdId, AiActionScope act, long charged, long chargeId, BillingOptions pricedAt,
        DateOnly chargedOn, int delivered, CancellationToken cancellationToken)
    {
        // delivered == 0 is its own case: CreditsFor floors the unit count at one, deliberately, so that a
        // bug upstream over-charges rather than zeroing a charge. Here nothing arrived, so nothing is kept.
        var keep = delivered <= 0 ? 0 : CreditPricing.CreditsFor(pricedAt, act.Action, delivered);
        var giveBack = charged - keep;
        if (giveBack <= 0) return;
        try
        {
            if (!await ledger.ReverseConsumptionAsync(householdId, giveBack,
                    CreditPricing.DescribeReversal(pricedAt, act.Action, delivered, act.Units), chargeId,
                    cancellationToken))
            {
                // ⚠️ Reachable, and it was not always: the ledger now refuses a reversal whose charge is
                // not this household's, is smaller than the amount asked for, or has already been given
                // back. Those refusals log their own reason at Error; this line says the give-back did not
                // happen, which is what an operator chasing a household's missing credits needs to see
                // beside them. Reporting a give-back the ledger never wrote is the one thing they would
                // believe without checking.
                logger.LogError("The reversal of {Credits} credit(s) for {Action} on household "
                    + "{HouseholdId} wrote no row.", giveBack, act.Action, householdId);
                return;
            }
            logger.LogInformation(
                "Gave back {Credits} credit(s) of {Charged} to household {HouseholdId} for {Action}: "
                + "{Delivered} of {Asked} unit(s) delivered.",
                giveBack, charged, householdId, act.Action, delivered, act.Units);
        }
        catch (Exception ex)
        {
            // ⚠️ No `catch (OperationCanceledException) { throw; }` here, and that is the house rule
            // deliberately not applied — the same call this file's RecordUsageAsync already makes, for the
            // same reason. The only caller is AiActionScope.DisposeAsync, which passes CancellationToken.None,
            // so there is no cancellation to honour: the clause could only rethrow a spontaneous one from
            // the SQLite layer, out of a `finally`, replacing a delivered answer with a crash — which is
            // precisely what the paragraph above says must not happen.
            logger.LogError(ex,
                "Couldn't give back {Credits} credit(s) to household {HouseholdId} for an undelivered "
                + "{Action}; they stay charged for work they did not receive.",
                giveBack, householdId, act.Action);
            return;
        }

        // The ledger has moved, so the operator's reconciliation has to move with it — otherwise /admin
        // reports this action earning credits the household no longer holds. Best-effort inside the meter,
        // and wrapped here too for the same reason margin.RecordAsync is: this runs from a `finally`, and
        // RecordReversalAsync deliberately rethrows cancellation, which would escape into the act's own
        // `await using` and replace a delivered answer with a crash.
        try
        {
            await margin.RecordReversalAsync(act.Action, chargedOn, giveBack, wholeCharge: keep == 0, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording the margin reversal for {Action} failed; reconciliation "
                + "over-states what it earned by {Credits} credit(s).", act.Action, giveBack);
        }
    }

    /// <summary>What one metered call did to the household's balance: whether it was on a billable path, and
    /// what it actually drew. Two facts rather than one, because they are not the same question — see
    /// <see cref="ServiceMarginDay.BillableCostMicros"/>.</summary>
    private readonly record struct CreditConsumption(bool Billable, long Credits)
    {
        /// <summary>Nothing to bill: a BYOK circuit, a billing-off box, a Founder, or no household.</summary>
        public static CreditConsumption None => new(false, 0);

        /// <summary>Billable, but drew nothing — a free action, or a later round of one already paid for.</summary>
        public static CreditConsumption Free => new(true, 0);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : inner.GetService(serviceType!, serviceKey);

    public void Dispose() => inner.Dispose();
}
