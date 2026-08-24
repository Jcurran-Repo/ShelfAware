using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;

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
        await RecordAsync(response.Usage, response.ModelId, cancellationToken);
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
        await RecordAsync(usage, model, cancellationToken);
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
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Deliberate: the user already has their answer — failing it over a bookkeeping write would
            // be worse than a quota under-count. Logged so a persistent metering problem is visible.
            logger.LogError(ex, "Recording AI usage failed; this call went unmetered.");
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : inner.GetService(serviceType!, serviceKey);

    public void Dispose() => inner.Dispose();
}
