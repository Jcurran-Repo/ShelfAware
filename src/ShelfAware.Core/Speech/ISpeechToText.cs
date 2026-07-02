namespace ShelfAware.Core.Speech;

/// <summary>
/// Transcribes one spoken utterance to text — the "ear" of the voice shell around the pantry chat.
/// Behind an interface so the provider is swappable and callers are testable without live API calls
/// (mirrors <see cref="Extraction.IReceiptExtractor"/>). The reasoning still lives in
/// <see cref="Chat.IPantryChat"/>; this is pure, swappable I/O.
/// </summary>
public interface ISpeechToText
{
    Task<SpeechToTextResult> TranscribeAsync(AudioClip audio, CancellationToken cancellationToken = default);
}

/// <param name="Data">Raw audio bytes of one captured utterance.</param>
/// <param name="MediaType">MIME type, e.g. audio/webm, audio/mp4, audio/mpeg, audio/wav.</param>
public record AudioClip(byte[] Data, string MediaType);

public record SpeechToTextResult
{
    public bool Success { get; init; }

    /// <summary>The transcribed text (empty on failure).</summary>
    public string Text { get; init; } = "";

    public string? Error { get; init; }

    public static SpeechToTextResult Ok(string text) => new() { Success = true, Text = text };

    public static SpeechToTextResult Fail(string error) => new() { Success = false, Error = error };
}
