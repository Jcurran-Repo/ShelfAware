using Microsoft.Extensions.Logging;
using ShelfAware.Core.Speech;

namespace ShelfAware.Llm;

/// <summary>
/// <see cref="ISpeechToText"/> over a Moonshine model running in this process — the ear that costs
/// nothing, needs no key, and sends nobody's kitchen anywhere.
///
/// <para>It takes a WAV, because that is the one container that can be read without a codec dependency
/// (<see cref="WaveAudio.Decode"/>), and the browser is where the conversion belongs: a MediaRecorder's
/// webm/opus decoded server-side would mean ffmpeg, which throws away the property that makes running
/// the model here worth doing at all. A clip in any other container is therefore a failure with a
/// sentence that says so, rather than a guess.</para>
///
/// <para>⚠️ Nothing here is metered and nothing is charged. The credit system prices what costs Jordan
/// money; a model on his own box costs a slice of a core.</para>
/// </summary>
public class MoonshineSpeechToText : ISpeechToText
{
    private readonly IMoonshineEngine _engine;
    private readonly ILogger<MoonshineSpeechToText> _logger;

    public MoonshineSpeechToText(IMoonshineEngine engine, ILogger<MoonshineSpeechToText> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public async Task<SpeechToTextResult> TranscribeAsync(
        AudioClip audio, CancellationToken cancellationToken = default)
    {
        if (audio.Data.Length == 0) return SpeechToTextResult.Fail("No audio to transcribe.");

        float[] samples;
        int sampleRate;
        try
        {
            (samples, sampleRate) = WaveAudio.Decode(audio.Data);
        }
        catch (InvalidDataException ex)
        {
            // The browser sent something this ear cannot open. Exception text to the log, plain copy to
            // the screen — the household can't fix a container mismatch and shouldn't be shown one.
            _logger.LogError(ex,
                "Moonshine was handed {Bytes} bytes of {MediaType}, which is not a PCM WAV. The capture "
                + "scripts convert to 16 kHz PCM before sending; a box seeing this has one that didn't.",
                audio.Data.Length, audio.MediaType);
            return SpeechToTextResult.Fail("Couldn't read that audio — please try again.");
        }

        // An empty listening window is not an error: the noise gate can pass a window that turns out to
        // hold no samples at all, and there is nothing to transcribe in it.
        if (samples.Length == 0) return SpeechToTextResult.Ok("");

        _logger.LogInformation(
            "Transcribing {Seconds:F2}s of audio at {SampleRate} Hz with Moonshine.",
            samples.Length / (double)sampleRate, sampleRate);

        try
        {
            var text = await _engine.TranscribeAsync(samples, sampleRate, cancellationToken);
            _logger.LogInformation("Transcribed {Chars} character(s).", text.Length);
            return SpeechToTextResult.Ok(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller walked away (the reader closed, the assistant stood down) — let it propagate.
            // ⚠️ Guarded on the TOKEN, for the reason ProviderCancellationSiteTests spells out: a
            // cancellation raised by anything other than the caller is a provider failure and must be
            // reported as one, not thrown out through a Blazor event handler with no ErrorBoundary
            // behind it. The engine's own bound arrives as a TimeoutException precisely so the two
            // cannot be confused here, but the guard stays: it is the rule, not a local optimisation.
            throw;
        }
        catch (Exception ex)
        {
            // Our own bound, a misconfiguration, a shutdown mid-call. Exception text to the log, plain
            // copy to the screen — the contract every provider here answers to (ProviderErrorCopyTests).
            // The model is local, so this most often means its files went missing after boot.
            _logger.LogError(ex, "Moonshine transcription failed.");
            return SpeechToTextResult.Fail("Couldn't make out that recording just now — please try again.");
        }
    }
}
