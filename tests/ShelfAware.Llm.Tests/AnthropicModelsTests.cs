namespace ShelfAware.Llm.Tests;

/// <summary>What the Settings picker suggests for Anthropic. A BYOK visitor's pick is the model their calls
/// run on, so the list is a promise, and <see cref="AnthropicThinking"/> is what it promises against.</summary>
public class AnthropicModelsTests
{
    [Fact]
    public void The_first_suggestion_is_the_apps_default_model() =>
        // A blank field fills with the first entry, so the first entry IS the default, not a copy of it.
        Assert.Equal(LlmOptions.DefaultModel, AnthropicModels.Offered[0]);

    [Fact]
    public void Every_suggested_model_runs_the_call_the_prompts_were_written_for() =>
        Assert.All(AnthropicModels.Offered, m => Assert.True(AnthropicThinking.RunsAsWritten(m), m));

    [Theory]
    [InlineData("claude-haiku-5-5", true)]           // thinking turned off by the rule
    [InlineData("claude-haiku-4-5", true)]           // never thinks unless asked
    [InlineData("claude-haiku-4-5-20251001", true)]
    [InlineData("claude-sonnet-5-5", false)]         // thinks by default, refuses thinking: disabled
    [InlineData("claude-opus-5-5", false)]
    [InlineData("CLAUDE-HAIKU-4-5", false)]          // matched as sent, like TurnsOff
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Which_models_run_as_written(string? model, bool expected) =>
        Assert.Equal(expected, AnthropicThinking.RunsAsWritten(model));
}
