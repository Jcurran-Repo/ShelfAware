using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// Drives <see cref="SherpaTextToSpeech"/> through a faked <see cref="ITtsEngine"/> — no model, no
/// native library — asserting what it asks the model to say and how it hands the result back. The
/// in-process analogue of the ElevenLabs half of <see cref="SpeechServicesTests"/>, and the reason the
/// engine is behind an interface at all: these are the observable facts (which words are spoken, what a
/// failure says, what the fingerprint means), and none of them needs 150 MB of weights to check.
///
/// <para>The engine's own behaviour — that it loads, that it refuses an out-of-range voice, that a cancel
/// actually stops a synthesis — is <see cref="SherpaKokoroEngineTests"/>, which needs the real model and
/// says so when it hasn't got one.</para>
/// </summary>
public class KokoroTextToSpeechTests
{
    private static SherpaTextToSpeech Tts(FakeKokoroEngine engine, KokoroSpeechOptions? o = null) =>
        new(engine, o ?? Model("kokoro-int8-en-v0_19"),
            NullLogger<SherpaTextToSpeech>.Instance);

    /// <summary>Options naming a model directory that need not exist: nothing here loads one.</summary>
    private static KokoroSpeechOptions Model(string archive) =>
        new() { ModelDirectory = Path.Combine(Path.GetTempPath(), "models", archive) };

    // ---- What actually gets spoken --------------------------------------------------------------

    [Fact]
    public async Task Synthesize_returns_the_models_samples_as_playable_wav()
    {
        var engine = FakeKokoroEngine.Returning([0f, 0.5f, -0.5f], sampleRate: 24000);

        var result = await Tts(engine).SynthesizeAsync("Sear the chicken.");

        Assert.True(result.Success);
        Assert.Equal(WaveAudio.MediaType, result.MediaType);
        Assert.Equal(WaveAudio.Encode([0f, 0.5f, -0.5f], 24000), result.Audio);
        Assert.Equal("Sear the chicken.", Assert.Single(engine.Spoken));
    }

    [Fact]
    public async Task Synthesize_blank_text_short_circuits_without_asking_the_model()
    {
        var engine = FakeKokoroEngine.Returning([1f]);
        var result = await Tts(engine).SynthesizeAsync("   ");

        Assert.False(result.Success);
        Assert.Empty(engine.Spoken);
    }

    // Text that is nothing but unspeakable punctuation normalizes to empty — don't spend a synthesis on it.
    [Fact]
    public async Task Synthesize_text_that_normalizes_to_nothing_short_circuits_without_asking_the_model()
    {
        var engine = FakeKokoroEngine.Returning([1f]);
        var result = await Tts(engine).SynthesizeAsync(" \t ");

        Assert.False(result.Success);
        Assert.Empty(engine.Spoken);
    }

    // The model reads the text literally, so numbers/units have to leave here already spoken — the same
    // treatment the ElevenLabs path gives, so a clip sounds the same whichever voiced it.
    [Fact]
    public async Task Synthesize_speaks_normalized_text_by_default()
    {
        var engine = FakeKokoroEngine.Returning([1f]);
        await Tts(engine).SynthesizeAsync("Simmer 6-7 min/side at 350°F");

        var spoken = Assert.Single(engine.Spoken);
        Assert.Contains("6 to 7 minutes per side", spoken);
        Assert.Contains("350 degrees Fahrenheit", spoken);
        Assert.DoesNotContain("6-7", spoken);
    }

    [Fact]
    public async Task Synthesize_leaves_text_alone_when_normalization_is_switched_off()
    {
        var engine = FakeKokoroEngine.Returning([1f]);
        var options = Model("kokoro-int8-en-v0_19");
        options.NormalizeText = false;

        await Tts(engine, options).SynthesizeAsync("Simmer 6-7 min/side");

        Assert.Contains("6-7 min/side", Assert.Single(engine.Spoken));
    }

    // Kokoro has no continuity-hint concept, so the neighbouring segments are not passed to it. Pinned
    // because the cache DOES key on them, and the two facts together are easy to misread as one.
    [Fact]
    public async Task Synthesize_speaks_only_this_segment_whatever_its_neighbours_are()
    {
        var engine = FakeKokoroEngine.Returning([1f]);
        await Tts(engine).SynthesizeAsync("Step 2. Sear the chicken.",
            new SpeechContext(Previous: "Step 1. Add oil.", Next: "Step 3. Rest."));

        var spoken = Assert.Single(engine.Spoken);
        Assert.DoesNotContain("Add oil", spoken);
        Assert.DoesNotContain("Rest", spoken);
    }

    // ---- Failure ---------------------------------------------------------------------------------

    [Fact]
    public async Task Synthesize_maps_a_model_failure_to_a_soft_failure()
    {
        var engine = FakeKokoroEngine.Throwing(new InvalidOperationException("the model is incomplete"));

        var result = await Tts(engine).SynthesizeAsync("hello");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // A caller that walks away (reader closed mid-narration) must see the cancel, not a soft failure.
    [Fact]
    public async Task Synthesize_propagates_the_callers_cancellation()
    {
        var engine = FakeKokoroEngine.Returning([1f]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Tts(engine).SynthesizeAsync("hello", null, cts.Token));
    }

    // ⚠️ The other half of that rule, and the one that is easy to get wrong: a cancellation the CALLER did
    // not ask for is a provider failure, and must be reported as one. Thrown out of the service instead, it
    // reaches a Blazor event handler with no ErrorBoundary behind it and takes the circuit down.
    [Fact]
    public async Task Synthesize_reports_a_cancellation_the_caller_did_not_ask_for_as_a_failure()
    {
        var engine = FakeKokoroEngine.Throwing(new OperationCanceledException());

        var result = await Tts(engine).SynthesizeAsync("hello");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // ⚠️ A run that returns no samples is a failure, not a very short clip. Returned as a success it is a
    // valid WAV of nothing, which the cache files and then serves forever — so the step plays as silence
    // with no error and no log line, and nobody can tell it from a quiet room.
    [Fact]
    public async Task Synthesize_treats_a_clip_with_no_samples_as_a_failure()
    {
        var result = await Tts(FakeKokoroEngine.Returning([])).SynthesizeAsync("Sear the chicken.");

        Assert.False(result.Success);
        Assert.Empty(result.Audio);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // The engine bounds itself so a household is never queued forever. A timeout is the provider failing,
    // not the caller leaving, so it must come back as a soft failure rather than out through the circuit.
    [Fact]
    public async Task Synthesize_reports_a_synthesis_that_timed_out_as_a_failure()
    {
        var engine = FakeKokoroEngine.Throwing(new TimeoutException("Kokoro gave up after 120s synthesizing."));

        var result = await Tts(engine).SynthesizeAsync("hello");

        Assert.False(result.Success);
        Assert.DoesNotContain("120s", result.Error!);
    }

    // ---- Fingerprint + media type -----------------------------------------------------------------

    [Fact]
    public void The_output_fingerprint_changes_with_anything_that_changes_the_audio()
    {
        var engine = FakeKokoroEngine.Returning([1f]);
        string Print(Action<KokoroSpeechOptions> change)
        {
            var o = Model("kokoro-int8-en-v0_19");
            change(o);
            return Tts(engine, o).OutputFingerprint;
        }

        var baseline = Print(_ => { });

        Assert.NotEqual(baseline, Print(o => o.SpeakerId = 3));
        Assert.NotEqual(baseline, Print(o => o.Speed = 0.8));
        Assert.NotEqual(baseline, Print(o => o.NormalizeText = false));
        Assert.NotEqual(baseline, Print(o => o.ModelFile = "model.onnx"));
        // A different archive is a different voice, whatever the settings around it say.
        Assert.NotEqual(baseline, Print(o => o.ModelDirectory = Path.Combine(Path.GetTempPath(), "models", "kokoro-multi-lang-v1_1")));
    }

    // ⚠️ Where the model was unpacked is not how it sounds. Keying on the full path would re-synthesize a
    // household's whole cookbook because someone moved a folder or wrote the same path with a trailing
    // slash — the cost of a free voice is CPU seconds, which is exactly what a cache exists to save.
    [Fact]
    public void The_output_fingerprint_ignores_where_the_model_was_unpacked()
    {
        var engine = FakeKokoroEngine.Returning([1f]);
        string Print(string directory)
        {
            var o = Model("unused");
            o.ModelDirectory = directory;
            return Tts(engine, o).OutputFingerprint;
        }

        var expected = Print(Path.Combine("opt", "shelfaware", "kokoro-int8-en-v0_19"));
        Assert.Equal(expected, Print(Path.Combine("home", "jordan", "models", "kokoro-int8-en-v0_19")));
        Assert.Equal(expected, Print(Path.Combine("opt", "shelfaware", "kokoro-int8-en-v0_19") + Path.DirectorySeparatorChar));
    }

    // NumThreads changes how LONG a synthesis takes, not what comes out of it.
    [Fact]
    public void The_output_fingerprint_ignores_the_thread_count()
    {
        var engine = FakeKokoroEngine.Returning([1f]);
        string Print(int threads)
        {
            var o = Model("kokoro-int8-en-v0_19");
            o.NumThreads = threads;
            return Tts(engine, o).OutputFingerprint;
        }

        Assert.Equal(Print(1), Print(8));
    }

    // A Kokoro clip must never be served for an ElevenLabs fingerprint (or vice versa): the provider name
    // leads the fingerprint precisely so the two namespaces can't collide.
    [Fact]
    public void The_output_fingerprint_is_namespaced_to_the_kokoro_provider() =>
        Assert.StartsWith("kokoro", Tts(FakeKokoroEngine.Returning([1f])).OutputFingerprint);

    // One container, always — there is no encoder in the process and nothing to choose between.
    [Fact]
    public void The_output_media_type_is_wav() =>
        Assert.Equal("audio/wav", Tts(FakeKokoroEngine.Returning([1f])).OutputMediaType);
}

/// <summary>A model that says whatever it was told to say, and remembers what it was asked.</summary>
internal sealed class FakeKokoroEngine : ITtsEngine
{
    private readonly Func<string, SynthesizedAudio> _speak;
    private readonly List<string> _spoken = [];

    private FakeKokoroEngine(Func<string, SynthesizedAudio> speak) => _speak = speak;

    /// <summary>What the engine was actually asked to say, in order.</summary>
    public IReadOnlyList<string> Spoken => _spoken;

    public static FakeKokoroEngine Returning(float[] samples, int sampleRate = 24000) =>
        new(_ => new SynthesizedAudio(samples, sampleRate));

    public static FakeKokoroEngine Throwing(Exception error) =>
        new(_ => throw error);

    public Task<SynthesizedAudio> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _spoken.Add(text);
        return Task.FromResult(_speak(text));
    }

    public void Dispose() { }
}
