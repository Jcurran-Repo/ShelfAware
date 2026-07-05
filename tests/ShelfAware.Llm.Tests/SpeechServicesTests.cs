using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// Drives the ElevenLabs speech services through a faked <see cref="HttpMessageHandler"/> — no live
/// API — asserting the request shape they send and how they map responses. The HTTP analogue of the
/// FakeChatClient tests for the Anthropic services.
/// </summary>
public class SpeechServicesTests
{
    private static readonly ElevenLabsOptions Defaults = new();

    // Base address only — the service attaches the xi-api-key PER REQUEST from IVoiceCredentials now
    // (matching Program.cs's ConfigureElevenLabs), so we no longer bake it onto the client.
    private static HttpClient Client(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.elevenlabs.io") };

    private static IOptions<ElevenLabsOptions> Opts(ElevenLabsOptions? o = null) => Options.Create(o ?? new ElevenLabsOptions());

    private static IVoiceCredentials Creds(string key = "test-key") => new FakeVoiceCredentials(key);

    private sealed record FakeVoiceCredentials(string ApiKey, string AgentId = "") : IVoiceCredentials;

    // ---- Speech-to-text (Scribe) ---------------------------------------------------------------

    [Fact]
    public async Task Transcribe_returns_the_text_field_and_posts_a_scribe_multipart_form()
    {
        var handler = FakeHttpMessageHandler.Returning(
            HttpResponses.Json("""{ "language_code": "en", "text": "mark shwarma and chipotle out" }"""));
        var stt = new ElevenLabsSpeechToText(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsSpeechToText>.Instance);

        var result = await stt.TranscribeAsync(new AudioClip(System.Text.Encoding.UTF8.GetBytes("FAKEAUDIO"), "audio/webm"));

        Assert.True(result.Success);
        Assert.Equal("mark shwarma and chipotle out", result.Text);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/speech-to-text", request.Uri.AbsolutePath);
        Assert.Equal("test-key", request.ApiKey);
        Assert.Equal("multipart/form-data", request.ContentType);
        Assert.Contains("model_id", request.Body);
        Assert.Contains(Defaults.SpeechToTextModel, request.Body); // scribe_v1
        Assert.Contains("file", request.Body);
    }

    [Fact]
    public async Task Transcribe_trims_whitespace_from_the_transcript()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Json("""{ "text": "  we're out of coffee\n" }"""));
        var stt = new ElevenLabsSpeechToText(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsSpeechToText>.Instance);

        var result = await stt.TranscribeAsync(new AudioClip([1, 2, 3], "audio/webm"));

        Assert.Equal("we're out of coffee", result.Text);
    }

    [Fact]
    public async Task Transcribe_empty_audio_short_circuits_without_a_call()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Json("""{ "text": "unused" }"""));
        var stt = new ElevenLabsSpeechToText(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsSpeechToText>.Instance);

        var result = await stt.TranscribeAsync(new AudioClip([], "audio/webm"));

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Transcribe_maps_an_http_error_to_a_soft_failure()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Error(HttpStatusCode.Unauthorized, "bad key"));
        var stt = new ElevenLabsSpeechToText(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsSpeechToText>.Instance);

        var result = await stt.TranscribeAsync(new AudioClip([1, 2, 3], "audio/webm"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Transcribe_without_a_key_fails_softly_and_makes_no_call()
    {
        // BYOK: no ElevenLabs key configured for this circuit → fail soft, don't hit the API.
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Json("""{ "text": "unused" }"""));
        var stt = new ElevenLabsSpeechToText(Client(handler), Opts(), Creds(""), NullLogger<ElevenLabsSpeechToText>.Instance);

        var result = await stt.TranscribeAsync(new AudioClip([1, 2, 3], "audio/webm"));

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    // ---- Text-to-speech ------------------------------------------------------------------------

    [Fact]
    public async Task Synthesize_returns_audio_bytes_and_posts_to_the_voice_endpoint()
    {
        byte[] audioBytes = [0x49, 0x44, 0x33, 0x04]; // "ID3" mp3 header-ish
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio(audioBytes, "audio/mpeg"));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        var result = await tts.SynthesizeAsync("Marked shwarma and chipotle out.");

        Assert.True(result.Success);
        Assert.Equal(audioBytes, result.Audio);
        Assert.Equal("audio/mpeg", result.MediaType);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/v1/text-to-speech/{Defaults.VoiceId}", request.Uri.AbsolutePath);
        Assert.Contains("output_format=" + Defaults.OutputFormat, request.Uri.Query);
        Assert.Equal("test-key", request.ApiKey);
        Assert.Contains("\"text\"", request.Body);
        Assert.Contains(Defaults.TextToSpeechModel, request.Body); // eleven_flash_v2_5
    }

    [Fact]
    public async Task Synthesize_blank_text_short_circuits_without_a_call()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        var result = await tts.SynthesizeAsync("   ");

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Synthesize_maps_an_http_error_to_a_soft_failure()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Error(HttpStatusCode.InternalServerError));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        var result = await tts.SynthesizeAsync("hello");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
