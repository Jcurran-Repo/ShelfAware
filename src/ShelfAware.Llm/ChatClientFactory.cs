using System.ClientModel;
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
    IChatClient Create(AiProvider provider, string apiKey, string model, string? baseUrl = null);
}

public sealed class ChatClientFactory : IChatClientFactory
{
    public IChatClient Create(AiProvider provider, string apiKey, string model, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No API key configured for {provider}. Add one in Settings.");

        return provider switch
        {
            // Both SDKs ship a Microsoft.Extensions.AI adapter, so the rest of the app depends only on
            // IChatClient and the provider is a pure swap here.
            AiProvider.Anthropic => new AnthropicClient { ApiKey = apiKey }.AsIChatClient(model),
            AiProvider.OpenAI => new OpenAIClient(apiKey).GetChatClient(model).AsIChatClient(),
            AiProvider.OpenAICompatible => LocalOpenAiCompatible(apiKey, model, baseUrl),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider."),
        };
    }

    // A locally run or self-hosted OpenAI-compatible server (Ollama, LM Studio, llama.cpp, vLLM). Same
    // OpenAI SDK, just pointed at the caller's base URL. Many local runtimes ignore the key, but the SDK
    // still requires a non-empty credential (the null/blank check above), so a placeholder like "local" is
    // fine. The base URL must be an absolute URI.
    private static IChatClient LocalOpenAiCompatible(string apiKey, string model, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException(
                "A base URL (e.g. http://localhost:11434/v1) is required for a local / OpenAI-compatible provider.");

        var options = new OpenAIClientOptions { Endpoint = endpoint };
        return new OpenAIClient(new ApiKeyCredential(apiKey), options).GetChatClient(model).AsIChatClient();
    }
}
