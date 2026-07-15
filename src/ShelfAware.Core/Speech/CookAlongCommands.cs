namespace ShelfAware.Core.Speech;

/// <summary>What the cook wants the reader to do. <see cref="None"/> means "not a reader command" —
/// the utterance is a real question and belongs to the model.</summary>
public enum CookAlongIntent
{
    /// <summary>Not a control phrase. Hand it to the brain.</summary>
    None,
    Next,
    Back,
    Repeat,
    /// <summary>"hold on" — stop reading, but keep listening for them to come back.</summary>
    Hold,
    /// <summary>"I'm back" — carry on from where we stopped.</summary>
    Resume,
    /// <summary>End the cook-along entirely.</summary>
    Stop,
}

/// <summary>
/// Plain-code intent matching for the hands-free reader. This is the reason the built-in cook-along can
/// be FASTER than a realtime agent: "next" is the overwhelmingly common turn, and here it costs a string
/// comparison and a cached clip — no model call, no round-trip, no per-minute meter. Only genuine
/// questions ("can I use butter instead?") reach the brain, where a second of latency is fine.
///
/// The WHOLE utterance must be the command (minus filler), same discipline as
/// <see cref="VoiceCommands.IsStop"/>. That's what keeps "what's next" a command while "what's next
/// after the salt goes in" stays a question for the model — a substring match would eat both.
/// </summary>
public static class CookAlongCommands
{
    private static readonly string[] Next =
    [
        "next", "next step", "next one", "go on", "go ahead", "continue", "keep going", "carry on",
        "move on", "onward", "then", "then what", "and then", "what's next", "what is next",
        "whats next", "what do i do next", "what next", "done", "got it", "did it", "that's done",
        "thats done", "finished", "i'm done with that", "im done with that",
    ];

    private static readonly string[] Back =
    [
        "back", "go back", "back up", "step back", "previous", "previous step", "last step",
        "go back a step", "one step back", "before that",
    ];

    private static readonly string[] Repeat =
    [
        "repeat", "repeat that", "again", "say again", "say that again", "read that again",
        "one more time", "come again", "what was that", "what did you say", "sorry what",
    ];

    private static readonly string[] Hold =
    [
        "hold on", "hold up", "wait", "wait a minute", "wait a sec", "wait a second", "hang on",
        "one second", "one sec", "just a second", "just a sec", "give me a second", "give me a minute",
        "pause", "stop for a second", "hold",
    ];

    private static readonly string[] Resume =
    [
        "i'm back", "im back", "back now", "ready", "i'm ready", "im ready", "resume", "carry on now",
        "let's go", "lets go", "go",
    ];

    /// <summary>Ends the reader specifically — <see cref="VoiceCommands.IsStop"/> covers the general
    /// "stop listening" phrases and is checked too.</summary>
    private static readonly string[] Stop =
    [
        "stop reading", "stop the recipe", "stop cooking", "stop the cook along", "stop cook along",
        "close the recipe", "i'm done cooking", "im done cooking", "never mind", "nevermind", "cancel",
    ];

    /// <summary>
    /// The reader command in <paramref name="transcript"/>, or <see cref="CookAlongIntent.None"/> if it
    /// isn't one. Note <see cref="CookAlongIntent.Next"/> and <see cref="CookAlongIntent.Resume"/>
    /// overlap in real speech ("go ahead" means carry on either way) — the caller resolves that against
    /// what the reader is currently doing, because only it knows whether we're paused.
    /// </summary>
    public static CookAlongIntent Match(string? transcript)
    {
        var core = Utterance.Core(transcript);
        if (core.Length == 0) return CookAlongIntent.None;

        // Stop is checked first: an utterance that ends the session must never be read as anything else.
        if (Has(Stop, core) || VoiceCommands.IsStop(transcript)) return CookAlongIntent.Stop;
        if (Has(Hold, core)) return CookAlongIntent.Hold;
        if (Has(Resume, core)) return CookAlongIntent.Resume;
        if (Has(Repeat, core)) return CookAlongIntent.Repeat;
        if (Has(Back, core)) return CookAlongIntent.Back;
        if (Has(Next, core)) return CookAlongIntent.Next;
        return CookAlongIntent.None;
    }

    /// <summary>
    /// Whether an utterance is worth waking the brain for. A listening window in a kitchen catches the
    /// extractor fan, a running tap, and the cook humming; the speech-to-text of that is empty or a
    /// stray syllable. Without this floor every clatter would buy an LLM call and an answer to a
    /// question nobody asked.
    /// </summary>
    public static bool IsWorthAsking(string? transcript) => Utterance.WordCount(transcript) >= 2;

    private static bool Has(string[] phrases, string core) => phrases.Contains(core, StringComparer.Ordinal);
}
