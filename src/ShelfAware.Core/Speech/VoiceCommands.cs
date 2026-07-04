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

    // Filler tolerated around a stop phrase ("okay, stop listening please" still stops).
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
        { "ok", "okay", "please", "now", "thanks", "thank", "you", "and" };

    /// <summary>
    /// True when the utterance is, in essence, a command to end the voice session. The WHOLE
    /// utterance must be the phrase (minus leading/trailing filler) — a sentence that merely
    /// contains it ("we're out of milk, then stop listening") still goes to the model so the
    /// statement isn't lost; the user can end on the next turn.
    /// </summary>
    public static bool IsStop(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return false;
        var tokens = Tokenize(transcript);
        int start = 0, end = tokens.Count;
        while (start < end && Filler.Contains(tokens[start])) start++;
        while (end > start && Filler.Contains(tokens[end - 1])) end--;
        var core = string.Join(' ', tokens.Skip(start).Take(end - start));
        return StopPhrases.Contains(core, StringComparer.Ordinal);
    }

    private static List<string> Tokenize(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '\'' ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
}
