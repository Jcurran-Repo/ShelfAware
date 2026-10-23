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
    [InlineData("CLAUDE-HAIKU-5-5", true)]
    [InlineData(" claude-haiku-5-5 ", true)]
    [InlineData("claude-haiku-4-5-20251001", false)] // never thinks unless asked
    [InlineData("claude-sonnet-5-5", false)]         // rejects thinking: disabled with a 400
    [InlineData("claude-opus-5-5", false)]           // likewise
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Thinking_is_turned_off_only_where_it_is_on_by_default_and_off_is_accepted(string? model, bool off) =>
        Assert.Equal(off, AnthropicThinking.TurnsOff(model));

    [Fact]
    public void A_haiku_5_5_call_is_sent_with_thinking_off()
    {
        var sent = AnthropicThinking.Apply(new ChatOptions { ModelId = "claude-haiku-5-5", MaxOutputTokens = 64 }, "claude-haiku-5-5");

        Assert.Equal(ReasoningEffort.None, sent!.Reasoning!.Effort);
        Assert.Equal(64, sent.MaxOutputTokens); // the rest of the call is untouched
    }

    [Fact]
    public void A_call_with_no_options_takes_the_model_the_client_was_built_for()
    {
        Assert.Equal(ReasoningEffort.None, AnthropicThinking.Apply(null, "claude-haiku-5-5")!.Reasoning!.Effort);
        Assert.Null(AnthropicThinking.Apply(null, "claude-sonnet-5-5"));
    }

    [Fact]
    public void The_model_a_call_names_wins_over_the_one_the_client_was_built_for()
    {
        // The adapter sends ChatOptions.ModelId when set, so the decision has to follow the same model:
        // the extraction model can differ from the chat model the client was bound to.
        var toSonnet = new ChatOptions { ModelId = "claude-sonnet-5-5" };
        Assert.Same(toSonnet, AnthropicThinking.Apply(toSonnet, "claude-haiku-5-5"));

        var toHaiku = AnthropicThinking.Apply(new ChatOptions { ModelId = "claude-haiku-5-5" }, "claude-sonnet-5-5");
        Assert.Equal(ReasoningEffort.None, toHaiku!.Reasoning!.Effort);
    }

    [Fact]
    public void A_caller_that_chose_its_own_reasoning_keeps_it()
    {
        var own = new ChatOptions { ModelId = "claude-haiku-5-5", Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } };
        Assert.Same(own, AnthropicThinking.Apply(own, "claude-haiku-5-5"));
        Assert.Equal(ReasoningEffort.Low, own.Reasoning!.Effort);
    }

    [Fact]
    public void The_callers_options_are_never_mutated()
    {
        // The services reuse one ChatOptions across a retry and across the chat's tool loop.
        var mine = new ChatOptions { ModelId = "claude-haiku-5-5" };
        var sent = AnthropicThinking.Apply(mine, "claude-haiku-5-5");

        Assert.NotSame(mine, sent);
        Assert.Null(mine.Reasoning);
    }

    [Fact]
    public async Task The_client_sends_every_call_through_the_rule()
    {
        var inner = FakeChatClient.Returning(new ChatResponse(new ChatMessage(ChatRole.Assistant, "a")),
                                             new ChatResponse(new ChatMessage(ChatRole.Assistant, "b")));
        using var client = new AnthropicThinkingChatClient(inner, "claude-haiku-5-5");

        await client.GetResponseAsync("hi", new ChatOptions { ModelId = "claude-haiku-5-5" });
        await client.GetResponseAsync("hi", new ChatOptions { ModelId = "claude-sonnet-5-5" });

        Assert.Equal(ReasoningEffort.None, inner.ReceivedOptions[0]!.Reasoning!.Effort);
        Assert.Null(inner.ReceivedOptions[1]!.Reasoning);
    }

    [Fact]
    public void The_factory_wraps_the_anthropic_client_in_the_rule()
    {
        var client = new ChatClientFactory().Create(AiProvider.Anthropic, "sk-test", "claude-haiku-5-5");
        Assert.IsType<AnthropicThinkingChatClient>(client);
    }
}
