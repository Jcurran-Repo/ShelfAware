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
    IEntitlements entitlements,
    ICurrentHousehold currentHousehold,
    ILogger<MeteredChatClient> logger) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await EnsureManagedCallAllowedAsync(cancellationToken);
        await ReserveCallAsync();
        ChatResponse? response = null;
        try
        {
            response = await inner.GetResponseAsync(messages, options, cancellationToken);
            return response;
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
        await ReserveCallAsync();
        UsageDetails? usage = null;
        string? model = null;
        try
        {
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
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
            // Uncancellable tail record (see GetResponseAsync) — runs on normal completion AND when the
            // consumer stops early, so a dropped stream that already yielded its usage can't dodge the write.
            if (usage is not null)
                await RecordUsageAsync(usage, model ?? options?.ModelId);
        }
    }

    /// <summary>The managed-call gate, consulted BEFORE the provider call (phase 4b) — the CHECKS only; the
    /// reserve is <see cref="ReserveCallAsync"/>, next. BYOK circuits skip it entirely (their key, their
    /// wallet). For a managed household: the per-household caps, then the demo box-wide valve, then
    /// <see cref="IEntitlements.IsAiAllowedAsync"/> — always true where billing is off (§7), and otherwise a
    /// Founder (unlimited) or a positive balance (running the lazy monthly allowance first). Throws to
    /// refuse — the provider call never happens, and (because the reserve runs after) nothing is counted.</summary>
    private async Task EnsureManagedCallAllowedAsync(CancellationToken cancellationToken)
    {
        if (!settings.Managed) return;
        await meter.EnsureLlmCallAllowedAsync(cancellationToken);
        // The demo box's BOX-WIDE daily valve (a no-op unless a Demo cap is configured) — the wallet bound
        // the per-household cap above can't give under open registration. Throws the come-back message.
        await demoMeter.EnsureCallAllowedAsync(cancellationToken);
        if (!await entitlements.IsAiAllowedAsync(cancellationToken))
            throw new AiCreditsExhaustedException();
    }

    /// <summary>Count one call, BEFORE the provider call and UNCANCELLABLY, so a client that aborts
    /// mid-flight still counts against the caps that bound volume — an aborted call still cost the key. The
    /// per-household count runs for BOTH modes (a BYOK visitor's own usage is recorded-but-never-limited,
    /// like their tokens); the box-wide demo valve counts host-key (managed) calls only. Best-effort like
    /// every usage write — a rare bookkeeping hiccup mustn't block a legitimate call, and the key's own spend
    /// limit is the hard backstop. Tokens/cost/credit can't be reserved here (they need the response); they
    /// record at the tail (<see cref="RecordUsageAsync"/>).</summary>
    private async Task ReserveCallAsync()
    {
        try { await meter.ReserveLlmCallAsync(CancellationToken.None); }
        catch (Exception ex) { logger.LogError(ex, "Reserving the AI call for the household usage row failed; it went uncounted."); }

        if (settings.Managed)
        {
            try { await demoMeter.RecordCallAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogError(ex, "Reserving the demo box-wide call failed; it went uncounted for the daily valve."); }
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

        try
        {
            await meter.RecordLlmUsageAsync(inputTokens, outputTokens, costMicros, CancellationToken.None);
        }
        catch (Exception ex) { logger.LogError(ex, "Recording AI usage failed; this call's tokens/cost went unrecorded."); }

        try
        {
            await RecordCreditConsumptionAsync(costMicros, model, CancellationToken.None);
        }
        catch (Exception ex) { logger.LogError(ex, "Recording credit consumption failed; this call didn't draw the balance."); }
    }

    /// <summary>Draw the household's credit balance down by this call's RETAIL cost — but only for a
    /// household that actually spends host credits: a MANAGED deployment with BILLING enabled (BYOK visitors
    /// ride their own key; a managed box with no <c>Payments</c> config is unlimited-by-default per §7, so
    /// the credit system doesn't apply and nothing is drawn) and a NON-unlimited tier (a Founder's cost is
    /// recorded above for the operator, but they never spend credit). ⚠️ The billing-off skip mirrors
    /// <see cref="IEntitlements.IsAiAllowedAsync"/>'s <c>!IsConfigured</c> short-circuit — the credit system
    /// is on or off as ONE thing (gate, pre-check, display, AND this recorder), so a billing-off box never
    /// accrues an invisible negative balance that flipping billing on would later enforce. This RECORDS
    /// consumption; the balance ENFORCEMENT is <see cref="EnsureManagedCallAllowedAsync"/> (phase 4b), which
    /// runs BEFORE the call — so this post-call hot path reads no balance.</summary>
    private async Task RecordCreditConsumptionAsync(long costMicros, string? model, CancellationToken cancellationToken)
    {
        if (!settings.Managed || !payments.Value.IsConfigured || costMicros <= 0) return;
        if ((await entitlements.GetTierAsync(cancellationToken)).IsUnlimited()) return;

        var householdId = await currentHousehold.GetIdAsync(cancellationToken);
        if (householdId is null) return;

        var retailMicros = AiPricing.ToRetailMicros(billing.Value, costMicros);
        await ledger.RecordConsumptionAsync(householdId, retailMicros, model, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : inner.GetService(serviceType!, serviceKey);

    public void Dispose() => inner.Dispose();
}
