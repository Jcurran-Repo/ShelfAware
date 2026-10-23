using Microsoft.Extensions.AI;

namespace ShelfAware.Llm;

/// <summary>
/// Whether an Anthropic call runs with the model's thinking turned off — ONE answer, applied to every
/// request at the client the factory builds, so no AI service has to remember it and none can disagree.
///
/// <para>⚠️ Why off. Every prompt here was written for and measured on Claude Haiku 4.5, which never
/// thinks unless asked. Claude Haiku 5.5 thinks by DEFAULT, and on this SDK that breaks three things at
/// once: the thinking tokens count against <c>MaxOutputTokens</c>, so the tag advisor's 32- and 64-token
/// caps can be spent before a word of the answer; they are output tokens on the bill that 4.5 never ran
/// up; and the Anthropic 12.x adapter replays an omitted-text thinking block as a
/// <c>redacted_thinking</c> block carrying its signature, which is not what the API sent, on the very
/// next round of the chat's tool loop. Turning it off keeps the call that 4.5 made.</para>
///
/// <para>⚠️ Why a list and not every Anthropic model. Off is not accepted everywhere: Claude Sonnet 5.5
/// and Claude Opus 5.5 reject <c>thinking: disabled</c> with a 400, so applying this to the whole picker
/// would break the two larger models outright. A model gets this only where turning thinking off is both
/// meaningful and accepted. The adapter maps <see cref="ReasoningEffort.None"/> to
/// <c>thinking: {"type": "disabled"}</c>; any other effort maps to a token budget, which Haiku 5.5
/// rejects, so <c>None</c> is the only value this may ever set.</para>
/// </summary>
internal static class AnthropicThinking
{
    private static readonly HashSet<string> ThinksUnlessTurnedOff = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude-haiku-5-5",
    };

    /// <summary>Whether a call to <paramref name="modelId"/> is sent with thinking turned off.</summary>
    public static bool TurnsOff(string? modelId) =>
        modelId is not null && ThinksUnlessTurnedOff.Contains(modelId.Trim());

    /// <summary>The options a call is actually sent with. The model is the one the call names, else the
    /// one the client was built for — the same precedence the adapter uses to pick the model, so this can
    /// never turn thinking off for a model other than the one the request goes to. A caller that set its
    /// own <see cref="ChatOptions.Reasoning"/> keeps it, and the caller's instance is never mutated: the
    /// services reuse one <see cref="ChatOptions"/> across a retry or a tool loop.</summary>
    public static ChatOptions? Apply(ChatOptions? options, string boundModel)
    {
        if (options?.Reasoning is not null || !TurnsOff(options?.ModelId ?? boundModel))
            return options;

        var sent = options?.Clone() ?? new ChatOptions();
        sent.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
        return sent;
    }
}

/// <summary>Applies <see cref="AnthropicThinking"/> to every request on the Anthropic client the factory
/// builds — the one choke point both the managed client and a visitor's own-key client pass through.</summary>
internal sealed class AnthropicThinkingChatClient(IChatClient inner, string boundModel) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, AnthropicThinking.Apply(options, boundModel), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, AnthropicThinking.Apply(options, boundModel), cancellationToken);
}
