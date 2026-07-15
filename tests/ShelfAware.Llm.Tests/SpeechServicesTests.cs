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

    // Scribe tags non-speech audio INTO the text by default, so "next" plus a cough comes back as
    // "Next (coughing)" — which matches no command and reaches the model as a question while the recipe
    // sits there. We want words, not stage directions.
    [Fact]
    public async Task Transcribe_asks_scribe_not_to_tag_audio_events()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Json("""{ "text": "next" }"""));
        var stt = new ElevenLabsSpeechToText(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsSpeechToText>.Instance);

        await stt.TranscribeAsync(new AudioClip([1, 2, 3], "audio/webm"));

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("tag_audio_events", body);
        Assert.Contains("false", body);
    }

    // We only ever read `text`; word timings are work for them and payload for us.
    [Fact]
    public async Task Transcribe_does_not_ask_for_timestamps_it_never_reads()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Json("""{ "text": "next" }"""));
        var stt = new ElevenLabsSpeechToText(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsSpeechToText>.Instance);

        await stt.TranscribeAsync(new AudioClip([1, 2, 3], "audio/webm"));

        Assert.Contains("timestamps_granularity", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Transcribe_names_the_language_rather_than_making_scribe_guess_off_one_word()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Json("""{ "text": "next" }"""));
        var stt = new ElevenLabsSpeechToText(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsSpeechToText>.Instance);

        await stt.TranscribeAsync(new AudioClip([1, 2, 3], "audio/webm"));

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("language_code", body);
        Assert.Contains(Defaults.SpeechLanguage, body); // eng
    }

    // Blanking it must restore auto-detection rather than send an empty language.
    [Fact]
    public async Task Transcribe_omits_the_language_when_it_is_not_configured()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Json("""{ "text": "next" }"""));
        var stt = new ElevenLabsSpeechToText(
            Client(handler), Opts(new ElevenLabsOptions { SpeechLanguage = "" }), Creds(),
            NullLogger<ElevenLabsSpeechToText>.Instance);

        await stt.TranscribeAsync(new AudioClip([1, 2, 3], "audio/webm"));

        Assert.DoesNotContain("language_code", Assert.Single(handler.Requests).Body);
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

    // ---- Text-to-speech: what we actually send ---------------------------------------------------

    // The model does NOT normalize for us on Flash (ElevenLabs gate apply_text_normalization behind
    // Enterprise and disable it on Flash for latency), so the text has to leave here already spoken.
    [Fact]
    public async Task Synthesize_sends_normalized_text_not_the_raw_recipe_step()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        await tts.SynthesizeAsync("Simmer 6-7 min/side at 350°F");

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("6 to 7 minutes per side", body);
        Assert.Contains("350 degrees Fahrenheit", body);
        Assert.DoesNotContain("6-7", body);
    }

    [Fact]
    public async Task Synthesize_leaves_text_alone_when_normalization_is_switched_off()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var tts = new ElevenLabsTextToSpeech(
            Client(handler), Opts(new ElevenLabsOptions { NormalizeText = false }), Creds(),
            NullLogger<ElevenLabsTextToSpeech>.Instance);

        await tts.SynthesizeAsync("Simmer 6-7 min/side");

        Assert.Contains("6-7 min/side", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Synthesize_passes_the_neighbouring_segments_for_continuity_and_normalizes_them_too()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        await tts.SynthesizeAsync("Step 2. Sear the chicken.",
            new SpeechContext(Previous: "Step 1. Add 2 tbsp oil.", Next: "Step 3. Rest 5 min."));

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("previous_text", body);
        Assert.Contains("2 tablespoons oil", body);
        Assert.Contains("next_text", body);
        Assert.Contains("Rest 5 minutes", body);
    }

    [Fact]
    public async Task Synthesize_omits_continuity_hints_when_there_is_no_context()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        await tts.SynthesizeAsync("A one-line confirmation.");

        var body = Assert.Single(handler.Requests).Body;
        Assert.DoesNotContain("previous_text", body);
        Assert.DoesNotContain("next_text", body);
    }

    // The cache reads OutputFingerprint on every synthesis, so it throwing isn't a flatter voice — it's
    // all TTS down. And it has to agree with what actually gets SENT: no voice_settings must not
    // fingerprint the same as the defaults, or two different requests would share a cache key.
    [Fact]
    public void The_output_fingerprint_survives_absent_voice_settings_without_pretending_they_were_sent()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1]));
        ElevenLabsTextToSpeech Tts(ElevenLabsOptions o) =>
            new(Client(handler), Opts(o), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        var absent = Tts(new ElevenLabsOptions { VoiceSettings = null! }).OutputFingerprint;

        Assert.NotNull(absent);
        Assert.NotEqual(Tts(new ElevenLabsOptions()).OutputFingerprint, absent);
    }

    // Changing how text is spoken has to retire clips voiced the old way, or the cache serves yesterday's
    // pronunciation forever. That's what SpeechText.Version rides in the fingerprint for.
    [Fact]
    public void The_output_fingerprint_changes_with_anything_that_changes_the_audio()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1]));
        string Print(ElevenLabsOptions o) =>
            new ElevenLabsTextToSpeech(Client(handler), Opts(o), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance)
                .OutputFingerprint;

        var baseline = Print(new ElevenLabsOptions());

        Assert.NotEqual(baseline, Print(new ElevenLabsOptions { VoiceId = "other-voice" }));
        Assert.NotEqual(baseline, Print(new ElevenLabsOptions { TextToSpeechModel = "eleven_multilingual_v2" }));
        Assert.NotEqual(baseline, Print(new ElevenLabsOptions { OutputFormat = "opus_48000_128" }));
        Assert.NotEqual(baseline, Print(new ElevenLabsOptions { NormalizeText = false }));

        var slower = new ElevenLabsOptions();
        slower.VoiceSettings.Speed = 0.8;
        Assert.NotEqual(baseline, Print(slower));
    }

    // ...and NOT with the key. Whose key paid for a clip doesn't change how it sounds, and keying on it
    // would make every visitor re-buy audio the household already owns.
    [Fact]
    public void The_output_fingerprint_ignores_the_api_key()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1]));
        string Print(string key) =>
            new ElevenLabsTextToSpeech(Client(handler), Opts(), Creds(key), NullLogger<ElevenLabsTextToSpeech>.Instance)
                .OutputFingerprint;

        Assert.Equal(Print("key-one"), Print("key-two"));
    }

    [Fact]
    public async Task Synthesize_sends_the_configured_voice_settings()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        await tts.SynthesizeAsync("hello");

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("voice_settings", body);
        Assert.Contains("stability", body);
        Assert.Contains("speed", body);
    }

    // Each setting is nullable so one a given model rejects can be turned off in config without
    // taking the whole request down; all-null means send no voice_settings at all.
    [Fact]
    public async Task Synthesize_omits_a_voice_setting_that_is_switched_off()
    {
        var options = new ElevenLabsOptions();
        options.VoiceSettings.Speed = null;
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(options), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        await tts.SynthesizeAsync("hello");

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("stability", body);
        Assert.DoesNotContain("speed", body);
    }

    [Fact]
    public async Task Synthesize_omits_voice_settings_entirely_when_none_are_configured()
    {
        var options = new ElevenLabsOptions
        {
            VoiceSettings = new ElevenLabsVoiceSettings
            {
                Stability = null, SimilarityBoost = null, Style = null, UseSpeakerBoost = null, Speed = null,
            },
        };
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(options), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        await tts.SynthesizeAsync("hello");

        Assert.DoesNotContain("voice_settings", Assert.Single(handler.Requests).Body);
    }

    // Text that is nothing BUT unspeakable punctuation normalizes to empty — don't spend a call on it.
    [Fact]
    public async Task Synthesize_text_that_normalizes_to_nothing_short_circuits_without_a_call()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);

        var result = await tts.SynthesizeAsync(" \t ");

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    // A caller that walks away (reader closed mid-narration) must see the cancel, not a soft failure —
    // otherwise the read-aloud can't tell "you closed me" from "the API broke".
    [Fact]
    public async Task Synthesize_propagates_the_callers_cancellation()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var tts = new ElevenLabsTextToSpeech(Client(handler), Opts(), Creds(), NullLogger<ElevenLabsTextToSpeech>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tts.SynthesizeAsync("hello", null, cts.Token));
    }
}
