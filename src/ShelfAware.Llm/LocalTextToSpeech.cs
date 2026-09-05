using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;

namespace ShelfAware.Llm;

/// <summary>
/// <see cref="ITextToSpeech"/> over a self-hosted, OpenAI-compatible TTS sidecar (Kokoro-FastAPI):
/// POST {BaseUrl}/v1/audio/speech, returns encoded audio the browser plays directly. Typed
/// <see cref="HttpClient"/> like <see cref="ElevenLabsTextToSpeech"/>, whose shape this deliberately
/// follows so the two are the same to read.
///
/// <para>The sidecar runs on the box, so there is no per-request cost, no per-character fee, and no
/// per-circuit key — it is trusted local infrastructure. That is the whole point: $0 synthesis with
/// nothing to meter. It carries an optional static bearer only for the case of a secured shared sidecar.</para>
///
/// <para>Kokoro's OpenAI endpoint has no continuity-hint concept, so the neighbouring segments in
/// <see cref="SpeechContext"/> aren't sent — each step is voiced on its own. They still key the cache
/// upstream (harmless: at worst an edited neighbour re-synthesizes a clip that costs nothing anyway),
/// so the cache stays provider-agnostic.</para>
/// </summary>
public class LocalTextToSpeech : ITextToSpeech
{
    private readonly HttpClient _http;
    private readonly LocalSpeechOptions _options;
    private readonly ILogger<LocalTextToSpeech> _logger;

    public LocalTextToSpeech(HttpClient http, IOptions<LocalSpeechOptions> options, ILogger<LocalTextToSpeech> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>Includes NormalizeText (and, through it, SpeechText.Version) because it decides which words
    /// are actually spoken, so changing how we spell text out retires clips voiced the old way rather than
    /// serving them forever. Leads with the provider name so a Local clip can never be served for an
    /// ElevenLabs fingerprint or vice versa. Excludes the optional key — it doesn't affect the audio.</remarks>
    public string OutputFingerprint => string.Join('|',
        "kokoro",
        _options.Model,
        _options.Voice,
        _options.Format,
        _options.Speed.ToString(CultureInfo.InvariantCulture),
        _options.NormalizeText ? "norm" + SpeechText.Version : "raw");

    /// <inheritdoc />
    public string OutputMediaType => MediaTypeFor(_options.Format);

    public async Task<TextToSpeechResult> SynthesizeAsync(string text, SpeechContext? context = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return TextToSpeechResult.Fail("Nothing to speak.");

        // Spell numbers/units out here (unless switched off) so the sidecar reads "350 degrees", not "350
        // °F" — same treatment the ElevenLabs path gives, so a clip sounds the same whichever voiced it.
        var spoken = _options.NormalizeText ? SpeechText.ForSpeech(text) : text;
        if (string.IsNullOrWhiteSpace(spoken)) return TextToSpeechResult.Fail("Nothing to speak.");

        _logger.LogInformation("Synthesizing {Chars} character(s) via local TTS ({Model}, voice {Voice}).",
            spoken.Length, _options.Model, _options.Voice);

        var payload = new SpeechPayload
        {
            Model = _options.Model,
            Input = spoken,
            Voice = _options.Voice,
            ResponseFormat = _options.Format,
            Speed = _options.Speed,
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/speech")
            {
                Content = JsonContent.Create(payload),
            };
            // Only when a shared sidecar is secured; a loopback one needs no auth and sends nothing.
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Local TTS returned {Status}: {Body}", (int)response.StatusCode, Truncate(body));
                return TextToSpeechResult.Fail($"Text-to-speech failed ({(int)response.StatusCode}).");
            }

            var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? MediaTypeFor(_options.Format);
            _logger.LogInformation("Synthesized {Bytes} bytes of {MediaType}.", audio.Length, mediaType);
            return TextToSpeechResult.Ok(audio, mediaType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller walked away (reader closed mid-narration) — let it propagate. Guarded on the token
            // because HttpClient reports its own TIMEOUT as a TaskCanceledException too, and a timeout is a
            // soft failure we still want to report as one, below.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Text-to-speech call to the local sidecar failed.");
            return TextToSpeechResult.Fail(ex.Message);
        }
    }

    private sealed record SpeechPayload
    {
        [JsonPropertyName("model")] public required string Model { get; init; }

        [JsonPropertyName("input")] public required string Input { get; init; }

        [JsonPropertyName("voice")] public required string Voice { get; init; }

        [JsonPropertyName("response_format")] public required string ResponseFormat { get; init; }

        [JsonPropertyName("speed")] public double Speed { get; init; }
    }

    // response_format values are container names (mp3, wav, opus, flac); map to a MIME type, used only as
    // a fallback when the sidecar's response omits Content-Type.
    private static string MediaTypeFor(string format) => format.ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "opus" => "audio/ogg",
        "flac" => "audio/flac",
        "pcm" => "audio/pcm",
        _ => "application/octet-stream",
    };

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
