namespace ShelfAware.Core.Speech;

/// <summary>
/// Synthesizes spoken audio from text — the "mouth" of the voice shell. Used for the chat's one-line
/// read-back confirmation (v2.0) and recipe read-aloud (v2.x). Behind an interface so the provider is
/// swappable and callers are testable without live API calls.
/// </summary>
public interface ITextToSpeech
{
    Task<TextToSpeechResult> SynthesizeAsync(string text, SpeechContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// A stable identifier for everything about this synthesizer's configuration that changes the audio
    /// it produces — voice, model, encoding, delivery settings. Same text + same context + same
    /// fingerprint means the same audio, which is what lets a cache be correct rather than a gamble.
    ///
    /// It must NOT include the API key: whose key paid for a clip doesn't change how the clip sounds.
    /// It SHOULD change whenever the spoken form would — including the provider's own text handling —
    /// so that improving how we speak retires the clips we spoke the old way, instead of serving them
    /// forever.
    /// </summary>
    string OutputFingerprint { get; }

    /// <summary>The MIME type this synthesizer produces under its current configuration. Lets a caller
    /// (or a cache) describe stored audio without having to know the provider's encoding options.</summary>
    string OutputMediaType { get; }
}

/// <summary>
/// The text either side of what's being synthesized, when it's one segment of a longer narration.
/// Providers use it for prosodic continuity so segment N doesn't open with the cold, falling
/// intonation of a fresh sentence — without it, a step-by-step playlist sounds like a list of
/// unrelated announcements rather than someone reading a recipe.
/// </summary>
public sealed record SpeechContext(string? Previous = null, string? Next = null);

public record TextToSpeechResult
{
    public bool Success { get; init; }

    /// <summary>Encoded audio bytes ready to hand to the browser (empty on failure).</summary>
    public byte[] Audio { get; init; } = [];

    /// <summary>MIME type of <see cref="Audio"/>, e.g. audio/mpeg.</summary>
    public string MediaType { get; init; } = "";

    public string? Error { get; init; }

    public static TextToSpeechResult Ok(byte[] audio, string mediaType) =>
        new() { Success = true, Audio = audio, MediaType = mediaType };

    public static TextToSpeechResult Fail(string error) => new() { Success = false, Error = error };
}
