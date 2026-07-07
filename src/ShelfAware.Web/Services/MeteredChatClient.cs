using Microsoft.Extensions.AI;

namespace ShelfAware.Web.Services;

/// <summary>
/// The metering skin over <see cref="ByokChatClient"/> — active ONLY in managed mode, where every call
/// spends the host's key: checks the household's daily quota before the provider call and records
/// calls + tokens after it. BYOK circuits pass straight through (their key, their wallet — never
/// metered, never limited). Sits at the top of the IChatClient chain so every AI service (chat,
/// extraction, advisors) is covered without touching any of them.
/// </summary>
public sealed class MeteredChatClient(
    ByokChatClient inner,
    CircuitAiSettings settings,
    AiUsageMeter meter,
    ILogger<MeteredChatClient> logger) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!settings.Managed)
        {
            return await inner.GetResponseAsync(messages, options, cancellationToken);
        }

        await meter.EnsureLlmCallAllowedAsync(cancellationToken);
        var response = await inner.GetResponseAsync(messages, options, cancellationToken);
        await RecordAsync(response.Usage, cancellationToken);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!settings.Managed)
        {
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        await meter.EnsureLlmCallAllowedAsync(cancellationToken);
        UsageDetails? usage = null;
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            // Providers report usage in a trailing UsageContent update; remember the last one seen.
            foreach (var content in update.Contents)
            {
                if (content is UsageContent u) usage = u.Details;
            }
            yield return update;
        }
        await RecordAsync(usage, cancellationToken);
    }

    private async Task RecordAsync(UsageDetails? usage, CancellationToken cancellationToken)
    {
        try
        {
            await meter.RecordLlmCallAsync(usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0, cancellationToken);
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
