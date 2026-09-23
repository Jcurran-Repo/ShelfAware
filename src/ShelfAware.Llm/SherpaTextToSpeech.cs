using System.Globalization;
using Microsoft.Extensions.Logging;
using ShelfAware.Core.Speech;

namespace ShelfAware.Llm;

/// <summary>
/// <see cref="ITextToSpeech"/> over a local voice running IN THIS PROCESS (see <see cref="SherpaTtsEngine"/>).
/// $0 per call — no key, no per-character fee, nothing to meter, and nothing deployed beside the app.
/// Which model family speaks is <see cref="SherpaTtsOptions.Family"/>'s answer, and nothing in this class
/// needs to know more than that.
///
/// <para>This class is the policy half and owns everything a cache or a screen can observe: which words
/// are actually spoken, what a failure says to the person, and what the fingerprint means. The model
/// itself sits behind <see cref="ITtsEngine"/> so all of that is testable without one.</para>
///
/// <para>Neither local family has a continuity-hint concept, so the neighbouring segments in
/// <see cref="SpeechContext"/> aren't used — each step is voiced on its own. They still key the cache upstream (harmless: at worst an
/// edited neighbour re-synthesizes a clip that costs nothing anyway), so the cache stays
/// provider-agnostic.</para>
/// </summary>
public class SherpaTextToSpeech : ITextToSpeech
{
    private readonly ITtsEngine _engine;
    private readonly SherpaTtsOptions _options;
    private readonly ILogger<SherpaTextToSpeech> _logger;

    public SherpaTextToSpeech(
        ITtsEngine engine, SherpaTtsOptions options, ILogger<SherpaTextToSpeech> logger)
    {
        _engine = engine;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Leads with the family name so a Kokoro clip can never be served for a Piper fingerprint, or either
    /// for an ElevenLabs one. Carries NormalizeText (and through it <see cref="SpeechText.Version"/>) because it
    /// decides which words are actually spoken.
    /// <para>The model's identity is its ARCHIVE — the directory's own name (e.g.
    /// <c>kokoro-int8-en-v0_19</c>) plus the ONNX file inside it — not the full path it was unpacked to.
    /// Moving a model to a different disk does not change how it sounds, so it must not retire the clips
    /// it voiced; swapping in a different archive does, and it does.</para>
    /// <para>NumThreads is deliberately absent: it changes how LONG synthesis takes, not what comes out.</para>
    /// <para>A family may add parts of its own through
    /// <see cref="SherpaTtsOptions.FingerprintExtras"/> — Matcha's vocoder is a second file that decides
    /// how the voice sounds, and there is no other family it would mean anything to. Appended rather than
    /// interpolated, so a family with nothing to add produces exactly the string it produced before the
    /// hook existed and no household's cache is retired by adding one.</para>
    /// </remarks>
    public string OutputFingerprint => string.Join('|', new[]
        {
            _options.Family,
            ArchiveName,
            _options.ModelFile,
            _options.SpeakerId.ToString(CultureInfo.InvariantCulture),
            _options.Speed.ToString(CultureInfo.InvariantCulture),
            "wav",
            _options.NormalizeText ? "norm" + SpeechText.Version : "raw",
        }.Concat(_options.FingerprintExtras));

    /// <summary>The model directory's leaf name, which is the archive's name as sherpa-onnx ships it.
    /// Trailing separators are trimmed first so <c>/models/kokoro/</c> and <c>/models/kokoro</c> — the
    /// same model, written two ways — cannot fingerprint differently and silently re-synthesize everything.</summary>
    private string ArchiveName =>
        Path.GetFileName(_options.ModelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <inheritdoc />
    public string OutputMediaType => WaveAudio.MediaType;

    public async Task<TextToSpeechResult> SynthesizeAsync(
        string text, SpeechContext? context = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return TextToSpeechResult.Fail("Nothing to speak.");

        // Spell numbers/units out here (unless switched off) so the model reads "350 degrees", not "350
        // °F" — the same treatment the ElevenLabs path gives, so a clip sounds the same whichever voiced it.
        var spoken = _options.NormalizeText ? SpeechText.ForSpeech(text) : text;
        if (string.IsNullOrWhiteSpace(spoken)) return TextToSpeechResult.Fail("Nothing to speak.");

        _logger.LogInformation("Synthesizing {Chars} character(s) with {Family} (voice {SpeakerId}).",
            spoken.Length, _options.Family, _options.SpeakerId);

        try
        {
            var audio = await _engine.GenerateAsync(spoken, cancellationToken);

            // ⚠️ A run that returns no samples is a FAILURE, not a very short clip. Encoded and returned as
            // a success it would be a valid 44-byte WAV of nothing — which the cache would then file and
            // serve forever, so the step would play as silence with no error, no log line and no way for
            // anyone to tell it apart from a quiet room. There is no text that legitimately reaches here
            // and synthesizes to nothing: the blank cases were turned away above.
            if (audio.Samples.Length == 0)
            {
                _logger.LogError("{Family} returned no samples for {Chars} character(s).",
                    _options.Family, spoken.Length);
                return TextToSpeechResult.Fail("Couldn't reach text-to-speech just now — please try again.");
            }

            var bytes = WaveAudio.Encode(audio.Samples, audio.SampleRate);
            _logger.LogInformation("Synthesized {Seconds:F1}s of audio ({Bytes} bytes of {MediaType}).",
                audio.Samples.Length / (double)audio.SampleRate, bytes.Length, WaveAudio.MediaType);
            return TextToSpeechResult.Ok(bytes, WaveAudio.MediaType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller walked away (reader closed mid-narration) — let it propagate. Guarded on the token
            // for the same reason every sibling service guards it: a cancellation raised by anything OTHER
            // than the caller is a provider failure, and must be reported as one rather than thrown out
            // through a Blazor event handler that has no ErrorBoundary behind it.
            throw;
        }
        catch (Exception ex)
        {
            // Exception text to the log, plain copy to the screen — see AnthropicReceiptExtractor. The model
            // is local, so this most often means its files are missing or unreadable; the message says what
            // the person can do, and the log says which of the two it was.
            _logger.LogError(ex, "{Family} synthesis failed.", _options.Family);
            return TextToSpeechResult.Fail("Couldn't reach text-to-speech just now — please try again.");
        }
    }
}
