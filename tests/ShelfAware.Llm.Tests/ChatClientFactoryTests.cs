using System.Text.Json;
using Microsoft.Extensions.AI;
using ShelfAware.Llm;

namespace ShelfAware.Llm.Tests;

public class ChatClientFactoryTests
{
    private readonly ChatClientFactory _factory = new();

    [Fact]
    public void Builds_an_anthropic_client()
    {
        var client = _factory.Create(AiProvider.Anthropic, "sk-test", "claude-haiku-5-5");
        Assert.NotNull(client);
    }

    [Fact]
    public void Builds_an_openai_client()
    {
        // Proves the OpenAI provider is wired through the same IChatClient seam (construction only — no call).
        var client = _factory.Create(AiProvider.OpenAI, "sk-test", "gpt-4o-mini");
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_key_is_rejected(string key)
    {
        Assert.Throws<InvalidOperationException>(() => _factory.Create(AiProvider.Anthropic, key, "m"));
    }

    [Fact]
    public void A_local_provider_requires_a_valid_base_url()
    {
        // A locally run model (Ollama, LM Studio, …) needs an endpoint; a missing or malformed one is rejected.
        Assert.Throws<InvalidOperationException>(() => _factory.Create(AiProvider.OpenAICompatible, "local", "llama3.1"));
        Assert.Throws<InvalidOperationException>(() => _factory.Create(AiProvider.OpenAICompatible, "local", "llama3.1", "not-a-url"));
    }

    [Fact]
    public void A_local_provider_builds_a_client_from_a_base_url()
    {
        var client = _factory.Create(AiProvider.OpenAICompatible, "local", "llama3.1", "http://localhost:11434/v1");
        Assert.NotNull(client);
    }
}

public class AnthropicThinkingTests
{
    [Theory]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("CLAUDE-HAIKU-5-5", false)]  // the adapter sends the id as given, so the rule matches it as given
    [InlineData(" claude-haiku-5-5 ", false)]
    [InlineData("claude-haiku-4-5-20251001", false)] // never thinks unless asked
    [InlineData("claude-sonnet-5-5", false)]         // rejects thinking: disabled with a 400
    [InlineData("claude-opus-5-5", false)]           // likewise
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Thinking_is_turned_off_only_where_it_is_on_by_default_and_off_is_accepted(string? model, bool off) =>
        Assert.Equal(off, AnthropicThinking.TurnsOff(model));

    /// <summary>Sends one call through the factory's wrapping and returns the options the client beneath
    /// it received — what the adapter would have turned into a request.</summary>
    private static async Task<ChatOptions> Sent(ChatOptions? options, string boundModel = "claude-haiku-5-5")
    {
        var inner = FakeChatClient.Returning(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        using var client = AnthropicThinking.Wrap(inner, boundModel);
        await client.GetResponseAsync("hi", options);
        return inner.ReceivedOptions.Single()!;
    }

    [Fact]
    public async Task A_haiku_5_5_call_is_sent_with_thinking_off()
    {
        var sent = await Sent(new ChatOptions { ModelId = "claude-haiku-5-5", MaxOutputTokens = 64 });

        Assert.Equal(ReasoningEffort.None, sent.Reasoning!.Effort);
        Assert.Equal(64, sent.MaxOutputTokens); // the rest of the call is untouched
    }

    [Fact]
    public async Task A_call_with_no_options_takes_the_model_the_client_was_built_for()
    {
        Assert.Equal(ReasoningEffort.None, (await Sent(null, "claude-haiku-5-5")).Reasoning!.Effort);
        Assert.Null((await Sent(null, "claude-sonnet-5-5")).Reasoning);
    }

    [Fact]
    public async Task The_model_a_call_names_wins_over_the_one_the_client_was_built_for()
    {
        // The adapter sends ChatOptions.ModelId when set, so the decision has to follow the same model:
        // the extraction model can differ from the chat model the client was bound to.
        Assert.Null((await Sent(new ChatOptions { ModelId = "claude-sonnet-5-5" }, "claude-haiku-5-5")).Reasoning);
        Assert.Equal(ReasoningEffort.None,
            (await Sent(new ChatOptions { ModelId = "claude-haiku-5-5" }, "claude-sonnet-5-5")).Reasoning!.Effort);
    }

    [Fact]
    public async Task A_caller_that_chose_its_own_reasoning_keeps_it()
    {
        var own = new ChatOptions { ModelId = "claude-haiku-5-5", Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } };
        Assert.Equal(ReasoningEffort.Low, (await Sent(own)).Reasoning!.Effort);
    }

    [Fact]
    public async Task A_caller_that_set_only_how_reasoning_is_shown_still_gets_thinking_off()
    {
        // Output says nothing about WHETHER to think; with Effort null the adapter sends no thinking
        // config at all, and Haiku 5.5 would think.
        var shown = new ChatOptions { ModelId = "claude-haiku-5-5", Reasoning = new ReasoningOptions { Output = ReasoningOutput.None } };
        var sent = await Sent(shown);

        Assert.Equal(ReasoningEffort.None, sent.Reasoning!.Effort);
        Assert.Equal(ReasoningOutput.None, sent.Reasoning.Output);
        Assert.Null(shown.Reasoning!.Effort); // the caller's own ReasoningOptions is untouched
    }

    [Fact]
    public async Task The_callers_options_are_never_mutated()
    {
        // The services reuse one ChatOptions across a retry and across the chat's tool loop.
        var mine = new ChatOptions { ModelId = "claude-haiku-5-5" };
        var sent = await Sent(mine);

        Assert.NotSame(mine, sent);
        Assert.Null(mine.Reasoning);
    }

    [Fact]
    public async Task The_streaming_path_goes_through_the_rule_too()
    {
        var inner = new StreamRecordingChatClient();
        using var client = AnthropicThinking.Wrap(inner, "claude-haiku-5-5");

        await foreach (var _ in client.GetStreamingResponseAsync("hi", new ChatOptions { ModelId = "claude-haiku-5-5" })) { }

        Assert.Equal(ReasoningEffort.None, inner.Received!.Reasoning!.Effort);
    }

    // ⚠️ The two tests below are the ones that matter: everything above checks the ChatOptions handed
    // DOWN, and the rule is only right if the Anthropic adapter turns ReasoningEffort.None into
    // thinking: disabled — not a token budget (a 400 on Haiku 5.5) and not nothing (thinking on). An SDK
    // bump that changes that mapping fails here, on the request body the API would actually receive.
    [Fact]
    public async Task On_the_wire_a_haiku_5_5_call_is_sent_with_thinking_disabled()
    {
        using var body = await SentBody("claude-haiku-5-5");

        var thinking = body.RootElement.GetProperty("thinking");
        Assert.Equal("disabled", thinking.GetProperty("type").GetString());
        Assert.False(thinking.TryGetProperty("budget_tokens", out _));
    }

    [Fact]
    public async Task On_the_wire_a_sonnet_5_5_call_carries_no_thinking_config()
    {
        using var body = await SentBody("claude-sonnet-5-5");
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
    }

    private static async Task<JsonDocument> SentBody(string model)
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Json($$$"""
            {"id":"msg_1","type":"message","role":"assistant","model":"{{{model}}}",
             "content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,
             "usage":{"input_tokens":1,"output_tokens":1}}
            """));
        using var http = new HttpClient(handler);
        using var client = ChatClientFactory.AnthropicChatClient("sk-test", model, http); // the factory's own construction

        await client.GetResponseAsync("hi", new ChatOptions { ModelId = model, MaxOutputTokens = 64 });

        return JsonDocument.Parse(handler.Requests.Single().Body);
    }

    private sealed class StreamRecordingChatClient : IChatClient
    {
        public ChatOptions? Received { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Received = options;
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "a");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void The_factory_wraps_the_anthropic_client_in_the_rule()
    {
        using var client = new ChatClientFactory().Create(AiProvider.Anthropic, "sk-test", "claude-haiku-5-5");
        Assert.NotNull(client.GetService<ConfigureOptionsChatClient>());
    }
}
