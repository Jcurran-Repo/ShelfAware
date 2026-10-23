namespace ShelfAware.Llm;

/// <summary>The Anthropic models the Settings picker suggests to a BYOK visitor — ONE list, held next to
/// <see cref="AnthropicThinking"/> because what may go on it is that rule's answer, not the page's.</summary>
public static class AnthropicModels
{
    /// <summary>Only models the app runs as written (<see cref="AnthropicThinking.RunsAsWritten"/>), since a
    /// visitor's pick is the model their calls go to. The first entry is what a blank field fills with, so it
    /// is the app's default model.
    /// <para>⚠️ Claude Sonnet 5.5 and Claude Opus 5.5 are not on it: on the pinned Anthropic SDK (12.x) neither
    /// can be run the way the prompts were written — see <see cref="AnthropicThinking"/>. The field is free
    /// text, so a visitor can still type one; this list is what we suggest, so it must not suggest those.</para></summary>
    public static IReadOnlyList<string> Offered { get; } = [LlmOptions.DefaultModel, "claude-haiku-4-5-20251001"];
}
