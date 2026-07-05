using System.Net.Http.Json;
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

    public async Task<TextToSpeechResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return TextToSpeechResult.Fail("Nothing to speak.");
        if (string.IsNullOrWhiteSpace(_credentials.ApiKey))
            return TextToSpeechResult.Fail("Add your ElevenLabs key in Settings to use voice.");

        _logger.LogInformation("Synthesizing {Chars} character(s) via ElevenLabs TTS ({Model}, voice {Voice}).",
            text.Length, _options.TextToSpeechModel, _options.VoiceId);

        var url = $"/v1/text-to-speech/{_options.VoiceId}?output_format={_options.OutputFormat}";
        var payload = new { text, model_id = _options.TextToSpeechModel };

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Text-to-speech call to ElevenLabs failed.");
            return TextToSpeechResult.Fail(ex.Message);
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
