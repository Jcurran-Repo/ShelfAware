using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace ShelfAware.Llm;

/// <summary>
/// Builds an <see cref="IChatClient"/> for a chosen provider + API key + model. This is the seam that
/// makes the app provider-swappable and BYOK-ready: in local dev one client is built from config; under
/// BYOK a per-circuit client is built from the visitor's own key (so the app never ships or uses the
/// owner's keys). The model is bound here; the AI services also set it per call via ChatOptions.ModelId.
/// </summary>
public interface IChatClientFactory
{
    IChatClient Create(AiProvider provider, string apiKey, string model);
}

public sealed class ChatClientFactory : IChatClientFactory
{
    public IChatClient Create(AiProvider provider, string apiKey, string model)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No API key configured for {provider}. Add one in Settings.");

        return provider switch
        {
            // Both SDKs ship a Microsoft.Extensions.AI adapter, so the rest of the app depends only on
            // IChatClient and the provider is a pure swap here.
            AiProvider.Anthropic => new AnthropicClient { ApiKey = apiKey }.AsIChatClient(model),
            AiProvider.OpenAI => new OpenAIClient(apiKey).GetChatClient(model).AsIChatClient(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider."),
        };
    }
}
