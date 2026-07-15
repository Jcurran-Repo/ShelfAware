using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;

namespace ShelfAware.Llm;

/// <summary>
/// <see cref="ISpeechToText"/> over ElevenLabs Scribe (POST /v1/speech-to-text, multipart form).
/// Unlike the Anthropic services this isn't an <c>IChatClient</c> workload — speech is its own REST
/// API — so it rides a typed <see cref="HttpClient"/> (base address + xi-api-key header configured in
/// DI). Same interface-seam + faked-transport-test pattern as the rest of the AI layer.
/// </summary>
public class ElevenLabsSpeechToText : ISpeechToText
{
    private readonly HttpClient _http;
    private readonly ElevenLabsOptions _options;
    private readonly IVoiceCredentials _credentials;
    private readonly ILogger<ElevenLabsSpeechToText> _logger;

    public ElevenLabsSpeechToText(HttpClient http, IOptions<ElevenLabsOptions> options, IVoiceCredentials credentials, ILogger<ElevenLabsSpeechToText> logger)
    {
        _http = http;
        _options = options.Value;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<SpeechToTextResult> TranscribeAsync(AudioClip audio, CancellationToken cancellationToken = default)
    {
        if (audio.Data.Length == 0) return SpeechToTextResult.Fail("No audio to transcribe.");
        if (string.IsNullOrWhiteSpace(_credentials.ApiKey))
            return SpeechToTextResult.Fail("Add your ElevenLabs key in Settings to use voice.");

        _logger.LogInformation("Transcribing {Bytes} bytes of {MediaType} via Scribe ({Model}).",
            audio.Data.Length, audio.MediaType, _options.SpeechToTextModel);

        using var form = new MultipartFormDataContent
        {
            { new StringContent(_options.SpeechToTextModel), "model_id" },

            // Scribe tags non-speech audio into the TEXT by default — "Next (coughing)" comes back as
            // exactly that. We want the words someone said, not stage directions about the room: a cough
            // isn't a word, but it turned "next" into a two-word phrase that matched no command and got
            // handed to the model as if it were a question. Off.
            { new StringContent("false"), "tag_audio_events" },

            // We only ever read `text`. Asking for word timings we never look at is work for them and
            // payload for us.
            { new StringContent("none"), "timestamps_granularity" },
        };

        // Don't make it guess the language off one word — see ElevenLabsOptions.SpeechLanguage.
        if (!string.IsNullOrWhiteSpace(_options.SpeechLanguage))
            form.Add(new StringContent(_options.SpeechLanguage), "language_code");
        var file = new ByteArrayContent(audio.Data);
        file.Headers.ContentType = new MediaTypeHeaderValue(audio.MediaType);
        form.Add(file, "file", FileNameFor(audio.MediaType));

        try
        {
            // The key is attached per-request (the visitor's own, scoped to their circuit) rather than as
            // a baked default header, so it only ever rides the actual ElevenLabs call.
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/speech-to-text") { Content = form };
            request.Headers.Add("xi-api-key", _credentials.ApiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Scribe returned {Status}: {Body}", (int)response.StatusCode, Truncate(body));
                return SpeechToTextResult.Fail($"Speech-to-text failed ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            _logger.LogInformation("Transcribed {Chars} character(s).", text.Length);
            return SpeechToTextResult.Ok(text.Trim());
        }
        catch (Exception ex)
        {
            // Transport / parse errors — fail soft at the I/O boundary (there's no useful retry here).
            _logger.LogError(ex, "Speech-to-text call to ElevenLabs failed.");
            return SpeechToTextResult.Fail(ex.Message);
        }
    }

    // ElevenLabs infers the codec from the uploaded file; give the part a matching extension so it does.
    private static string FileNameFor(string mediaType) => mediaType switch
    {
        "audio/webm" => "audio.webm",
        "audio/mp4" or "audio/x-m4a" => "audio.mp4",
        "audio/mpeg" => "audio.mp3",
        "audio/wav" or "audio/x-wav" => "audio.wav",
        "audio/ogg" => "audio.ogg",
        _ => "audio.bin",
    };

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
