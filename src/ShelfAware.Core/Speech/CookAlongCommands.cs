using System.Text.RegularExpressions;

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
    /// <summary>"go to step 3" — jump somewhere specific. The step rides in
    /// <see cref="CookAlongCommand.Step"/>.</summary>
    GoToStep,
    /// <summary>"hold on" — I'm busy; ignore the room until I say otherwise.</summary>
    Hold,
    /// <summary>"I'm back" — carry on.</summary>
    Resume,
    /// <summary>End the cook-along entirely.</summary>
    Stop,
}

/// <summary>
/// A matched command. Most carry nothing but the intent; <see cref="CookAlongIntent.GoToStep"/> also
/// carries where to go, which is why this is a record rather than a bare enum.
/// </summary>
/// <param name="Step">Target for <see cref="CookAlongIntent.GoToStep"/>: 1-based, or 0 for "start over"
/// (the intro). NOT range-checked here — Core doesn't know how long the recipe is. A caller that gets a
/// step it doesn't have should hand the utterance to the brain, which does know, and can say so.</param>
public readonly record struct CookAlongCommand(CookAlongIntent Intent, int? Step = null)
{
    public static readonly CookAlongCommand None = new(CookAlongIntent.None);

    public static implicit operator CookAlongCommand(CookAlongIntent intent) => new(intent);
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
        "next", "next step", "next one", "up next", "on to the next", "onto the next", "next up",
        "go on", "go ahead", "continue", "keep going", "carry on", "move on", "move along", "onward",
        "then", "then what", "and then", "what's next", "what is next", "whats next",
        "what do i do next", "what next", "done", "got it", "did it", "that's done", "thats done",
        "finished", "i'm done with that", "im done with that",
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

    private static readonly string[] StartOver =
    [
        "start over", "start again", "from the top", "start from the beginning", "begin again",
        "back to the start", "back to the beginning",
    ];

    // "step one" is left to StepTarget below — this is only the phrasings a number regex can't reach.
    private static readonly string[] FirstStep = ["first step", "the first step"];

    // "step 3", "go to step 3", "read me step three", "jump to step 5", "back to step 2".
    // Anchored, because the whole utterance must be the command: "what goes in at step 3" is a question.
    private static readonly Regex StepTarget = new(
        @"^(?:(?:go|jump|skip|move|take me|read me|read|start)\s+)?(?:(?:back|over)\s+)?(?:(?:to|at|from|on)\s+)?step\s+(?<n>\w+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Speech-to-text may hand back either "step 3" or "step three" for the same words.
    private static readonly string[] NumberWords =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
        "nineteen", "twenty",
    ];

    /// <summary>
    /// The reader command in <paramref name="transcript"/>, or <see cref="CookAlongIntent.None"/> if it
    /// isn't one. Note <see cref="CookAlongIntent.Next"/> and <see cref="CookAlongIntent.Resume"/>
    /// overlap in real speech ("go ahead" means carry on either way) — the caller resolves that against
    /// what the reader is currently doing, because only it knows.
    /// </summary>
    public static CookAlongCommand Match(string? transcript)
    {
        var core = Utterance.Core(transcript);
        if (core.Length == 0) return CookAlongCommand.None;

        // Stop is checked first: an utterance that ends the session must never be read as anything else.
        if (Has(Stop, core) || VoiceCommands.IsStop(transcript)) return CookAlongIntent.Stop;
        if (Has(Hold, core)) return CookAlongIntent.Hold;
        if (Has(Resume, core)) return CookAlongIntent.Resume;
        if (Has(StartOver, core)) return new CookAlongCommand(CookAlongIntent.GoToStep, 0);
        if (Has(FirstStep, core)) return new CookAlongCommand(CookAlongIntent.GoToStep, 1);
        if (StepNumberIn(core) is { } step) return new CookAlongCommand(CookAlongIntent.GoToStep, step);
        if (Has(Repeat, core)) return CookAlongIntent.Repeat;
        if (Has(Back, core)) return CookAlongIntent.Back;
        if (Has(Next, core)) return CookAlongIntent.Next;
        return CookAlongCommand.None;
    }

    /// <summary>The step number in an explicit jump, or null if this isn't one.</summary>
    private static int? StepNumberIn(string core)
    {
        var m = StepTarget.Match(core);
        if (!m.Success) return null;

        var n = m.Groups["n"].Value;
        if (int.TryParse(n, out var digits)) return digits >= 0 ? digits : null;

        var word = Array.IndexOf(NumberWords, n);
        return word >= 0 ? word : null;
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
