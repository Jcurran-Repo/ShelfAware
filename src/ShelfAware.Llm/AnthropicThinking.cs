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
    // Ordinal and untrimmed on purpose: the adapter sends the model id exactly as given, so the rule
    // matches exactly what goes on the wire. Haiku 5.5 has no dated snapshot or alias to also list.
    private static readonly HashSet<string> ThinksUnlessTurnedOff = new(StringComparer.Ordinal)
    {
        "claude-haiku-5-5",
    };

    // Models that never think unless a call asks them to, so a call that says nothing runs as it did on 4.5.
    private static readonly HashSet<string> ThinksOnlyWhenAsked = new(StringComparer.Ordinal)
    {
        "claude-haiku-4-5",
        "claude-haiku-4-5-20251001",
    };

    /// <summary>Whether a call to <paramref name="modelId"/> is sent with thinking turned off.</summary>
    public static bool TurnsOff(string? modelId) =>
        modelId is not null && ThinksUnlessTurnedOff.Contains(modelId);

    /// <summary>Whether a call to <paramref name="modelId"/> makes the call the prompts were written for: either
    /// this rule turns its thinking off, or it never thinks unless asked. What <see cref="AnthropicModels.Offered"/>
    /// may hold. Sonnet 5.5 and Opus 5.5 are neither — they think by default and refuse to stop.</summary>
    public static bool RunsAsWritten(string? modelId) =>
        TurnsOff(modelId) || (modelId is not null && ThinksOnlyWhenAsked.Contains(modelId));

    /// <summary>The Anthropic client the factory hands out: <paramref name="inner"/> behind the framework's
    /// <c>ConfigureOptions</c> decorator, which clones the caller's options (or makes empty ones) and runs
    /// <see cref="Configure"/> on the clone, for streaming and non-streaming calls alike. The caller's own
    /// instance is never touched — the services reuse one <see cref="ChatOptions"/> across a retry and
    /// across the chat's tool loop.</summary>
    public static IChatClient Wrap(IChatClient inner, string boundModel) =>
        inner.AsBuilder().ConfigureOptions(sent => Configure(sent, boundModel)).Build();

    /// <summary>Applies the rule to the options a call is about to be sent with. The model is the one the
    /// call names, else the one the client was built for — the same precedence the adapter uses to pick
    /// the model, so this can never turn thinking off for a model other than the one the request goes to.
    /// A caller that chose its own <see cref="ReasoningOptions.Effort"/> keeps it; one that set only
    /// <see cref="ReasoningOptions.Output"/> has said nothing about WHETHER to think, so it gets the rule
    /// with its Output kept.</summary>
    internal static void Configure(ChatOptions sent, string boundModel)
    {
        if (sent.Reasoning?.Effort is not null || !TurnsOff(sent.ModelId ?? boundModel)) return;

        sent.Reasoning ??= new ReasoningOptions();
        sent.Reasoning.Effort = ReasoningEffort.None;
    }
}
