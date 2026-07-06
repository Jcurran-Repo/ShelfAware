using Microsoft.Extensions.AI;
using ShelfAware.Llm;

namespace ShelfAware.Web.Services;

/// <summary>
/// A per-circuit <see cref="IChatClient"/> that builds the real provider client from the visitor's
/// <see cref="CircuitAiSettings"/> at CALL time (not at construction) — so it doesn't matter that the
/// browser settings are loaded after the circuit's component graph is created. The AI services keep
/// depending only on <see cref="IChatClient"/> (their code is unchanged); this is the one place a
/// visitor's own key becomes their own calls. The underlying client is cached and rebuilt only if the
/// settings change.
/// </summary>
public sealed class ByokChatClient(CircuitAiSettings settings, IChatClientFactory factory) : IChatClient
{
    private IChatClient? _inner;
    private (AiProvider Provider, string Key, string Model, string? BaseUrl) _builtFor;

    private IChatClient Inner()
    {
        var current = (settings.Provider, settings.ApiKey, settings.ChatModel, settings.BaseUrl);
        if (_inner is null || _builtFor != current)
        {
            _inner?.Dispose();
            // Throws a friendly "add a key in Settings" if none is set — surfaced by the AI service's
            // own try/catch as a normal error, not a crash.
            _inner = factory.Create(settings.Provider, settings.ApiKey, settings.ChatModel, settings.BaseUrl);
            _builtFor = current;
        }
        return _inner;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Inner().GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Inner().GetStreamingResponseAsync(messages, options, cancellationToken);

    // Don't force-build the inner client just to answer a service query (it might have no key yet).
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : _inner?.GetService(serviceType!, serviceKey);

    public void Dispose() => _inner?.Dispose();
}
