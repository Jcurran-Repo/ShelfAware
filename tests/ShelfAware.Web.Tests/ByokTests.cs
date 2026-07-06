using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ShelfAware.Llm;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>Per-circuit BYOK plumbing: settings default to server config, the visitor's browser overrides
/// them, and the delegating client builds each visitor's own provider client from those settings.</summary>
public class ByokTests
{
    private static CircuitAiSettings Settings(string apiKey = "dev-key", string provider = "Anthropic",
        string extraction = "ex-model", string chat = "chat-model") =>
        new(Options.Create(new LlmOptions { Provider = provider, ApiKey = apiKey, ExtractionModel = extraction, ChatModel = chat }));

    [Fact]
    public void Settings_default_to_the_server_config()
    {
        var s = Settings();
        Assert.Equal(AiProvider.Anthropic, s.Provider);
        Assert.Equal("dev-key", s.ApiKey);
        Assert.Equal("ex-model", s.ExtractionModel);
        Assert.Equal("chat-model", s.ChatModel);
        Assert.True(s.HasKey);
        Assert.False(s.FromBrowser);
    }

    [Fact]
    public void Apply_overlays_the_visitors_settings()
    {
        var s = Settings();
        s.Apply(AiProvider.OpenAI, "visitor-key", "gpt-extract", "gpt-chat");

        Assert.Equal(AiProvider.OpenAI, s.Provider);
        Assert.Equal("visitor-key", s.ApiKey);
        Assert.Equal("gpt-extract", s.ExtractionModel);
        Assert.Equal("gpt-chat", s.ChatModel);
        Assert.True(s.FromBrowser);
    }

    [Fact]
    public void Apply_with_blank_models_keeps_the_defaults()
    {
        var s = Settings(extraction: "keep-ex", chat: "keep-chat");
        s.Apply(AiProvider.Anthropic, "k", extractionModel: "", chatModel: null);

        Assert.Equal("keep-ex", s.ExtractionModel);
        Assert.Equal("keep-chat", s.ChatModel);
    }

    [Fact]
    public async Task Delegating_client_builds_from_settings_and_rebuilds_only_on_change()
    {
        var settings = Settings(apiKey: "k1", chat: "m1");
        var factory = new RecordingFactory();
        var client = new ByokChatClient(settings, factory);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal((AiProvider.Anthropic, "k1", "m1", (string?)null), Assert.Single(factory.Calls));

        // Same settings → the cached client is reused, no rebuild.
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "again")]);
        Assert.Single(factory.Calls);

        // Settings changed (a new visitor key/provider) → rebuild.
        settings.Apply(AiProvider.OpenAI, "k2", null, "m2");
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "third")]);
        Assert.Equal(2, factory.Calls.Count);
        Assert.Equal((AiProvider.OpenAI, "k2", "m2", (string?)null), factory.Calls[1]);
    }

    [Fact]
    public void A_browser_base_url_is_ignored_unless_the_server_allows_custom_endpoints()
    {
        // Public demo (default): custom endpoints off — a visitor's base URL must NOT take effect (no SSRF).
        var locked = Settings();
        locked.Apply(AiProvider.OpenAICompatible, "local", null, "llama3.1", "http://evil.internal/v1");
        Assert.Null(locked.BaseUrl);

        // Self-host opts in — now the base URL is honored.
        var open = new CircuitAiSettings(Options.Create(
            new LlmOptions { ApiKey = "dev-key", AllowCustomEndpoint = true }));
        open.Apply(AiProvider.OpenAICompatible, "local", null, "llama3.1", "http://localhost:11434/v1");
        Assert.Equal("http://localhost:11434/v1", open.BaseUrl);
    }

    [Fact]
    public async Task Delegating_client_threads_the_base_url_for_a_local_provider()
    {
        var settings = new CircuitAiSettings(Options.Create(
            new LlmOptions { ApiKey = "dev-key", ChatModel = "c", AllowCustomEndpoint = true }));
        settings.Apply(AiProvider.OpenAICompatible, "local", null, "llama3.1", "http://localhost:11434/v1");
        var factory = new RecordingFactory();
        var client = new ByokChatClient(settings, factory);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal((AiProvider.OpenAICompatible, "local", "llama3.1", "http://localhost:11434/v1"),
            Assert.Single(factory.Calls));
    }

    [Fact]
    public async Task Delegating_client_surfaces_a_friendly_error_when_no_key_is_set()
    {
        var client = new ByokChatClient(Settings(apiKey: ""), new RecordingFactory());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
    }

    private sealed class RecordingFactory : IChatClientFactory
    {
        public List<(AiProvider Provider, string Key, string Model, string? BaseUrl)> Calls { get; } = [];

        public IChatClient Create(AiProvider provider, string apiKey, string model, string? baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException($"No API key configured for {provider}.");
            Calls.Add((provider, apiKey, model, baseUrl));
            return new StubChatClient();
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
