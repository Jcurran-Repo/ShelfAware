namespace ShelfAware.Core.Speech;

/// <summary>
/// Synthesizes spoken audio from text — the "mouth" of the voice shell. Used for the chat's one-line
/// read-back confirmation (v2.0) and recipe read-aloud (v2.x). Behind an interface so the provider is
/// swappable and callers are testable without live API calls.
/// </summary>
public interface ITextToSpeech
{
    Task<TextToSpeechResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default);
}

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
