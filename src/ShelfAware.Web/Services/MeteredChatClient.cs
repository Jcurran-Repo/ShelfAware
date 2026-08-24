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
        try
        {
            var inputTokens = usage?.InputTokenCount ?? 0;
            var outputTokens = usage?.OutputTokenCount ?? 0;
            // Stamp the cost at CALL time from the configured rate, so a later rate change never rewrites
            // this row's cost (docs §4). An unreported model falls back (never free) — see AiPricing.
            var costMicros = AiPricing.CostMicros(billing.Value, model, inputTokens, outputTokens);
            await meter.RecordLlmCallAsync(inputTokens, outputTokens, costMicros, cancellationToken);
            await RecordCreditConsumptionAsync(costMicros, model, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Deliberate: the user already has their answer — failing it over a bookkeeping write would
            // be worse than a quota under-count. Logged so a persistent metering problem is visible.
            logger.LogError(ex, "Recording AI usage or credit failed; this call went unmetered.");
        }
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
