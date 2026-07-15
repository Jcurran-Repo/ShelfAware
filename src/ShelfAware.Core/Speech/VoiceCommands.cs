namespace ShelfAware.Core.Speech;

/// <summary>
/// Plain-code voice-command detection. Control phrases like "stop listening" are matched in C#
/// BEFORE the transcript reaches the LLM — ending a session must be instant, deterministic, and
/// free (thesis: plain code where it suffices).
/// </summary>
public static class VoiceCommands
{
    private static readonly string[] StopPhrases =
    [
        "stop listening", "stop the conversation", "end the conversation", "end conversation",
        "stop talking", "goodbye", "good bye", "that's all", "that is all", "we're done", "we are done",
    ];

    /// <summary>
    /// True when the utterance is, in essence, a command to end the voice session. The WHOLE
    /// utterance must be the phrase (minus leading/trailing filler — see <see cref="Utterance"/>) —
    /// a sentence that merely contains it ("we're out of milk, then stop listening") still goes to
    /// the model so the statement isn't lost; the user can end on the next turn.
    /// </summary>
    public static bool IsStop(string? transcript) =>
        StopPhrases.Contains(Utterance.Core(transcript), StringComparer.Ordinal);
}
