using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;

namespace ShelfAware.Llm;

/// <summary>
/// <see cref="ITextToSpeech"/> over ElevenLabs TTS (POST /v1/text-to-speech/{voice_id}). Typed
/// <see cref="HttpClient"/> (speech isn't an <c>IChatClient</c> workload). Returns encoded audio
/// bytes the browser can play directly.
/// </summary>
public class ElevenLabsTextToSpeech : ITextToSpeech
{
    private readonly HttpClient _http;
    private readonly ElevenLabsOptions _options;
    private readonly IVoiceCredentials _credentials;
    private readonly ILogger<ElevenLabsTextToSpeech> _logger;

    public ElevenLabsTextToSpeech(HttpClient http, IOptions<ElevenLabsOptions> options, IVoiceCredentials credentials, ILogger<ElevenLabsTextToSpeech> logger)
    {
        _http = http;
        _options = options.Value;
        _credentials = credentials;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>Includes NormalizeText because it decides what words are actually spoken, so flipping it
    /// (or changing SpeechText's rules, via its Version) retires clips voiced under the old behaviour
    /// rather than serving them forever. Deliberately excludes the API key — it doesn't affect the audio.</remarks>
    public string OutputFingerprint
    {
        get
        {
            // Null-tolerant to match VoiceSettingsPayload.From below, and tolerant the SAME way: null
            // there means "send no voice_settings", so null here must fingerprint as "none sent" rather
            // than as the defaults — otherwise two genuinely different requests would share a cache key.
            // It matters that this can't throw: the cache reads it on EVERY synthesis, so a null would
            // take all TTS down rather than merely flatten the voice.
            var v = _options.VoiceSettings;
            return string.Join('|',
                "elevenlabs",
                _options.TextToSpeechModel,
                _options.VoiceId,
                _options.OutputFormat,
                _options.NormalizeText ? "norm" + SpeechText.Version : "raw",
                Num(v?.Stability), Num(v?.SimilarityBoost), Num(v?.Style),
                v?.UseSpeakerBoost?.ToString() ?? "-", Num(v?.Speed));

            static string Num(double? d) => d?.ToString(CultureInfo.InvariantCulture) ?? "-";
        }
    }

    /// <inheritdoc />
    public string OutputMediaType => MediaTypeFor(_options.OutputFormat);

    public async Task<TextToSpeechResult> SynthesizeAsync(string text, SpeechContext? context = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return TextToSpeechResult.Fail("Nothing to speak.");
        if (string.IsNullOrWhiteSpace(_credentials.ApiKey))
            return TextToSpeechResult.Fail("Add your ElevenLabs key in Settings to use voice.");

        // The model does no normalization for us on Flash — see ElevenLabsOptions.NormalizeText. The
        // neighbouring segments get the same treatment so the continuity hints match what is actually spoken.
        var spoken = Speakable(text);
        if (string.IsNullOrWhiteSpace(spoken)) return TextToSpeechResult.Fail("Nothing to speak.");

        _logger.LogInformation("Synthesizing {Chars} character(s) via ElevenLabs TTS ({Model}, voice {Voice}).",
            spoken.Length, _options.TextToSpeechModel, _options.VoiceId);

        var url = $"/v1/text-to-speech/{_options.VoiceId}?output_format={_options.OutputFormat}";
        var payload = new TtsPayload
        {
            Text = spoken,
            ModelId = _options.TextToSpeechModel,
            PreviousText = Speakable(context?.Previous),
            NextText = Speakable(context?.Next),
            VoiceSettings = VoiceSettingsPayload.From(_options.VoiceSettings),
        };

        try
        {
            // Per-request key (the visitor's own, scoped to their circuit) rather than a baked default header.
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
            request.Headers.Add("xi-api-key", _credentials.ApiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("ElevenLabs TTS returned {Status}: {Body}", (int)response.StatusCode, Truncate(body));
                return TextToSpeechResult.Fail($"Text-to-speech failed ({(int)response.StatusCode}).");
            }

            var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? MediaTypeFor(_options.OutputFormat);
            _logger.LogInformation("Synthesized {Bytes} bytes of {MediaType}.", audio.Length, mediaType);
            return TextToSpeechResult.Ok(audio, mediaType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller walked away (e.g. the reader was closed mid-narration) — let it propagate.
            // Guarded on the token because HttpClient reports its own TIMEOUT as a TaskCanceledException
            // too, and a timeout is a soft failure we still want to report as one, below.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Text-to-speech call to ElevenLabs failed.");
            return TextToSpeechResult.Fail(ex.Message);
        }
    }

    /// <summary>Blank stays blank (an omitted continuity hint); otherwise normalize when configured to.</summary>
    private string? Speakable(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : _options.NormalizeText ? SpeechText.ForSpeech(text)
        : text;

    private sealed record TtsPayload
    {
        [JsonPropertyName("text")] public required string Text { get; init; }

        [JsonPropertyName("model_id")] public required string ModelId { get; init; }

        [JsonPropertyName("previous_text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PreviousText { get; init; }

        [JsonPropertyName("next_text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NextText { get; init; }

        [JsonPropertyName("voice_settings")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public VoiceSettingsPayload? VoiceSettings { get; init; }
    }

    private sealed record VoiceSettingsPayload
    {
        [JsonPropertyName("stability")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Stability { get; init; }

        [JsonPropertyName("similarity_boost")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? SimilarityBoost { get; init; }

        [JsonPropertyName("style")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Style { get; init; }

        [JsonPropertyName("use_speaker_boost")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? UseSpeakerBoost { get; init; }

        [JsonPropertyName("speed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Speed { get; init; }

        /// <summary>Null when nothing is configured, so the request omits voice_settings entirely
        /// rather than sending an empty object.</summary>
        public static VoiceSettingsPayload? From(ElevenLabsVoiceSettings? s)
        {
            if (s is null) return null;
            if (s.Stability is null && s.SimilarityBoost is null && s.Style is null
                && s.UseSpeakerBoost is null && s.Speed is null) return null;

            return new VoiceSettingsPayload
            {
                Stability = s.Stability,
                SimilarityBoost = s.SimilarityBoost,
                Style = s.Style,
                UseSpeakerBoost = s.UseSpeakerBoost,
                Speed = s.Speed,
            };
        }
    }

    // output_format values look like mp3_44100_128, opus_48000_128, ulaw_8000 — map the family to a MIME type,
    // used only as a fallback when the response omits Content-Type.
    private static string MediaTypeFor(string outputFormat) =>
        outputFormat.StartsWith("mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg"
        : outputFormat.StartsWith("opus", StringComparison.OrdinalIgnoreCase) ? "audio/opus"
        : outputFormat.StartsWith("ulaw", StringComparison.OrdinalIgnoreCase) ? "audio/basic"
        : "application/octet-stream";

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
