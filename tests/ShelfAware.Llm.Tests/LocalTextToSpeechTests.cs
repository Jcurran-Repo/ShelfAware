using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// Drives <see cref="LocalTextToSpeech"/> through a faked <see cref="FakeHttpMessageHandler"/> — no live
/// sidecar — asserting the OpenAI-compatible request it sends and how it maps responses. The local
/// analogue of the ElevenLabs half of <see cref="SpeechServicesTests"/>.
/// </summary>
public class LocalTextToSpeechTests
{
    private static readonly LocalSpeechOptions Defaults = new();

    // Base address is the sidecar origin; the service POSTs to the relative /v1/audio/speech (matching
    // ConfigureLocal in SpeechRegistration).
    private static HttpClient Client(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://127.0.0.1:8880") };

    private static LocalTextToSpeech Tts(FakeHttpMessageHandler handler, LocalSpeechOptions? o = null) =>
        new(Client(handler), Options.Create(o ?? new LocalSpeechOptions()), NullLogger<LocalTextToSpeech>.Instance);

    [Fact]
    public async Task Synthesize_returns_audio_bytes_and_posts_to_the_openai_speech_endpoint()
    {
        byte[] audioBytes = [0x49, 0x44, 0x33, 0x04]; // "ID3" mp3 header-ish
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio(audioBytes, "audio/mpeg"));
        var tts = Tts(handler);

        var result = await tts.SynthesizeAsync("Sear the chicken.");

        Assert.True(result.Success);
        Assert.Equal(audioBytes, result.Audio);
        Assert.Equal("audio/mpeg", result.MediaType);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/audio/speech", request.Uri.AbsolutePath);
        Assert.Contains("\"input\"", request.Body);
        Assert.Contains(Defaults.Model, request.Body);  // kokoro
        Assert.Contains(Defaults.Voice, request.Body);  // af_heart
        Assert.Contains("\"response_format\"", request.Body);
        Assert.Contains(Defaults.Format, request.Body); // mp3
        Assert.Contains("\"speed\"", request.Body);
    }

    [Fact]
    public async Task Synthesize_blank_text_short_circuits_without_a_call()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var result = await Tts(handler).SynthesizeAsync("   ");

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Synthesize_maps_an_http_error_to_a_soft_failure()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Error(HttpStatusCode.InternalServerError));
        var result = await Tts(handler).SynthesizeAsync("hello");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // The sidecar reads the text literally, so numbers/units have to leave here already spoken — the same
    // treatment the ElevenLabs path gives, so a clip sounds the same whichever voiced it.
    [Fact]
    public async Task Synthesize_sends_normalized_text_by_default()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        await Tts(handler).SynthesizeAsync("Simmer 6-7 min/side at 350°F");

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("6 to 7 minutes per side", body);
        Assert.Contains("350 degrees Fahrenheit", body);
        Assert.DoesNotContain("6-7", body);
    }

    [Fact]
    public async Task Synthesize_leaves_text_alone_when_normalization_is_switched_off()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        await Tts(handler, new LocalSpeechOptions { NormalizeText = false }).SynthesizeAsync("Simmer 6-7 min/side");

        Assert.Contains("6-7 min/side", Assert.Single(handler.Requests).Body);
    }

    // Kokoro's OpenAI endpoint has no continuity-hint concept, so unlike ElevenLabs we send none — pinned
    // so nobody "adds" fields the sidecar would reject.
    [Fact]
    public async Task Synthesize_sends_no_continuity_hints()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        await Tts(handler).SynthesizeAsync("Step 2. Sear the chicken.",
            new SpeechContext(Previous: "Step 1. Add oil.", Next: "Step 3. Rest."));

        var body = Assert.Single(handler.Requests).Body;
        Assert.DoesNotContain("previous_text", body);
        Assert.DoesNotContain("next_text", body);
    }

    // A loopback sidecar needs no auth: sending an empty bearer would be noise at best and a 401 at worst.
    [Fact]
    public async Task Synthesize_sends_no_authorization_when_no_key_is_configured()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        await Tts(handler).SynthesizeAsync("hello");

        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task Synthesize_sends_a_bearer_when_a_sidecar_key_is_configured()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        await Tts(handler, new LocalSpeechOptions { ApiKey = "shared-secret" }).SynthesizeAsync("hello");

        Assert.Equal("Bearer shared-secret", Assert.Single(handler.Requests).Authorization);
    }

    // Text that is nothing but unspeakable punctuation normalizes to empty — don't spend a call on it.
    [Fact]
    public async Task Synthesize_text_that_normalizes_to_nothing_short_circuits_without_a_call()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        var result = await Tts(handler).SynthesizeAsync(" \t ");

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    // A caller that walks away (reader closed mid-narration) must see the cancel, not a soft failure.
    [Fact]
    public async Task Synthesize_propagates_the_callers_cancellation()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1, 2, 3]));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Tts(handler).SynthesizeAsync("hello", null, cts.Token));
    }

    // ---- Fingerprint + media type ---------------------------------------------------------------

    [Fact]
    public void The_output_fingerprint_changes_with_anything_that_changes_the_audio()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1]));
        string Print(LocalSpeechOptions o) => Tts(handler, o).OutputFingerprint;

        var baseline = Print(new LocalSpeechOptions());

        Assert.NotEqual(baseline, Print(new LocalSpeechOptions { Voice = "am_michael" }));
        Assert.NotEqual(baseline, Print(new LocalSpeechOptions { Model = "kokoro-v1" }));
        Assert.NotEqual(baseline, Print(new LocalSpeechOptions { Format = "wav" }));
        Assert.NotEqual(baseline, Print(new LocalSpeechOptions { Speed = 0.8 }));
        Assert.NotEqual(baseline, Print(new LocalSpeechOptions { NormalizeText = false }));
    }

    // A Local clip must never be served for an ElevenLabs fingerprint (or vice versa): the provider name
    // leads the fingerprint precisely so the two namespaces can't collide.
    [Fact]
    public void The_output_fingerprint_is_namespaced_to_the_local_provider()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1]));
        Assert.StartsWith("kokoro", Tts(handler).OutputFingerprint);
    }

    // The optional sidecar key doesn't change how a clip sounds, so keying on it would make a secured box
    // re-buy audio it already has if the key rotated.
    [Fact]
    public void The_output_fingerprint_ignores_the_optional_sidecar_key()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1]));
        Assert.Equal(
            Tts(handler, new LocalSpeechOptions { ApiKey = "one" }).OutputFingerprint,
            Tts(handler, new LocalSpeechOptions { ApiKey = "two" }).OutputFingerprint);
    }

    [Theory]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("wav", "audio/wav")]
    [InlineData("opus", "audio/ogg")]
    [InlineData("flac", "audio/flac")]
    public void The_output_media_type_follows_the_format(string format, string expected)
    {
        var handler = FakeHttpMessageHandler.Returning(HttpResponses.Audio([1]));
        Assert.Equal(expected, Tts(handler, new LocalSpeechOptions { Format = format }).OutputMediaType);
    }
}
