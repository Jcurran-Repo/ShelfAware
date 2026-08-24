using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>
/// The metering skin over <see cref="ByokChatClient"/>. RECORDING is universal — every call's
/// tokens land in the household's usage row so the user can see what they've spent (the Settings
/// usage panel, the accuracy check's cost line). LIMITING is managed-mode only: quotas guard the
/// HOST's wallet, so BYOK circuits (their key, their wallet) are recorded but never blocked. Sits
/// at the top of the IChatClient chain so every AI service (chat, extraction, advisors) is covered
/// without touching any of them.
/// </summary>
public sealed class MeteredChatClient(
    ByokChatClient inner,
    CircuitAiSettings settings,
    AiUsageMeter meter,
    IOptions<BillingOptions> billing,
    CreditLedger ledger,
    IEntitlements entitlements,
    ICurrentHousehold currentHousehold,
    ILogger<MeteredChatClient> logger) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (settings.Managed)
        {
            await meter.EnsureLlmCallAllowedAsync(cancellationToken);
        }
        var response = await inner.GetResponseAsync(messages, options, cancellationToken);
        // Prefer the model the provider REPORTED; fall back to the one we REQUESTED before AiPricing's own
        // priciest-tier fallback — so a provider that doesn't echo the model still prices at the real
        // (usually cheaper) requested model, not Opus rates. (See the code-review hardening, phase 2.)
        await RecordAsync(response.Usage, response.ModelId ?? options?.ModelId, cancellationToken);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (settings.Managed)
        {
            await meter.EnsureLlmCallAllowedAsync(cancellationToken);
        }
        UsageDetails? usage = null;
        string? model = null;
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
        // Reported model, then the requested one (see GetResponseAsync), then AiPricing's fallback.
        await RecordAsync(usage, model ?? options?.ModelId, cancellationToken);
    }

    private async Task RecordAsync(UsageDetails? usage, string? model, CancellationToken cancellationToken)
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

        // Two INDEPENDENT best-effort writes on two SEPARATE databases — the AiUsage row (pantry) and the
        // credit ledger (auth) — which no single transaction can span. Each is guarded on its OWN so one
        // failing never drops the other: a pantry hiccup must not silently skip the MONEY (ledger) write,
        // which is what a single wrapping try/catch used to do. Cross-DB atomicity genuinely isn't
        // available here, so a write that fails after its sibling landed is logged and bounded to this one
        // call — the user already has their answer, and failing it over a bookkeeping write is worse.
        try
        {
            await meter.RecordLlmCallAsync(inputTokens, outputTokens, costMicros, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { logger.LogError(ex, "Recording AI usage failed; this call's cost went unrecorded."); }

        try
        {
            await RecordCreditConsumptionAsync(costMicros, model, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { logger.LogError(ex, "Recording credit consumption failed; this call didn't draw the balance."); }
    }

    /// <summary>Draw the household's credit balance down by this call's RETAIL cost — but only for a
    /// household that actually spends host credits: a MANAGED deployment (BYOK visitors ride their own
    /// key) and a NON-unlimited tier (a Founder's cost is recorded above for the operator, but they never
    /// spend credit). Mirrors the meter's gate exemption. Phase 2 RECORDS consumption; it does not yet
    /// ENFORCE the balance (gating is phase 4) — so no balance is read on this hot path.</summary>
    private async Task RecordCreditConsumptionAsync(long costMicros, string? model, CancellationToken cancellationToken)
    {
        if (!settings.Managed || costMicros <= 0) return;
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
