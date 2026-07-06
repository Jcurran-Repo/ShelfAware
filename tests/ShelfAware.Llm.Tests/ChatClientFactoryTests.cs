using ShelfAware.Llm;

namespace ShelfAware.Llm.Tests;

public class ChatClientFactoryTests
{
    private readonly ChatClientFactory _factory = new();

    [Fact]
    public void Builds_an_anthropic_client()
    {
        var client = _factory.Create(AiProvider.Anthropic, "sk-test", "claude-haiku-4-5-20251001");
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
