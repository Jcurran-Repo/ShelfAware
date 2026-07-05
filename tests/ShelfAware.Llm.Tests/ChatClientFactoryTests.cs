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
}
