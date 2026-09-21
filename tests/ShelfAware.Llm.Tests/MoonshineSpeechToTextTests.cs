using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Speech;
using ShelfAware.Llm;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// The in-process ear's decisions, tested without a 120 MB model in the runner — what counts as audio,
/// what the failure copy is, and whose cancellation a cancellation was. The model itself is proved by
/// <c>tools/MoonshineCheck</c> against a real archive, for the reason a model-dependent unit test is a
/// test that either fails on every machine without the download or skips itself and reports a green
/// nobody earned.
/// </summary>
public class MoonshineSpeechToTextTests
{
    private static MoonshineSpeechToText Ear(IMoonshineEngine engine) =>
        new(engine, NullLogger<MoonshineSpeechToText>.Instance);

    private static byte[] Wav(params float[] samples) => WaveAudio.Encode(samples, 16000);

    [Fact]
    public async Task It_hands_the_decoded_samples_and_their_rate_to_the_model()
    {
        var engine = new FakeMoonshineEngine("next step please");

        var result = await Ear(engine).TranscribeAsync(new AudioClip(Wav(0.5f, -0.5f, 0.25f), "audio/wav"));

        Assert.True(result.Success);
        Assert.Equal("next step please", result.Text);
        Assert.Equal(3, engine.LastSamples!.Length);
        Assert.Equal(16000, engine.LastSampleRate);
    }

    [Fact]
    public async Task Audio_that_is_not_a_wav_is_a_named_failure_and_never_reaches_the_model()
    {
        // ⚠️ The browser's fallback path (pcm.js returns null when it can't decode its own recording)
        // lands here carrying webm/opus. Transcribing those bytes as if they were samples would produce
        // confident gibberish, which reads as a broken model rather than a wrong container.
        var engine = new FakeMoonshineEngine("should not happen");

        var result = await Ear(engine).TranscribeAsync(new AudioClip([1, 2, 3, 4], "audio/webm"));

        Assert.False(result.Success);
        Assert.Equal("Couldn't read that audio — please try again.", result.Error);
        Assert.False(engine.WasCalled);
    }

    [Fact]
    public async Task No_audio_is_refused_before_anything_is_decoded()
    {
        var engine = new FakeMoonshineEngine("should not happen");

        var result = await Ear(engine).TranscribeAsync(new AudioClip([], "audio/wav"));

        Assert.False(result.Success);
        Assert.Equal("No audio to transcribe.", result.Error);
        Assert.False(engine.WasCalled);
    }

    [Fact]
    public async Task A_window_holding_no_samples_is_an_empty_answer_rather_than_a_failure()
    {
        // The noise gate can pass a window that turns out to be empty. Nothing was said; that is an
        // answer. Reporting it as an error would put "couldn't make that out" on screen for silence.
        var engine = new FakeMoonshineEngine("should not happen");

        var result = await Ear(engine).TranscribeAsync(new AudioClip(Wav(), "audio/wav"));

        Assert.True(result.Success);
        Assert.Equal("", result.Text);
        Assert.False(engine.WasCalled);
    }

    [Fact]
    public async Task Heard_nothing_from_the_model_is_also_an_answer()
    {
        var result = await Ear(new FakeMoonshineEngine("")).TranscribeAsync(new AudioClip(Wav(0.1f), "audio/wav"));

        Assert.True(result.Success);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task Our_own_timeout_is_reported_as_a_provider_failure_in_plain_copy()
    {
        var engine = new FakeMoonshineEngine(new TimeoutException("Moonshine gave up after 30s transcribing."));

        var result = await Ear(engine).TranscribeAsync(new AudioClip(Wav(0.1f), "audio/wav"));

        Assert.False(result.Success);
        Assert.Equal("Couldn't make out that recording just now — please try again.", result.Error);
        // Not the exception's own words: a household can't act on "gave up after 30s transcribing".
        Assert.DoesNotContain("30s", result.Error);
    }

    [Fact]
    public async Task A_missing_model_is_reported_rather_than_thrown_at_a_circuit()
    {
        var engine = new FakeMoonshineEngine(new InvalidOperationException("The Moonshine model is incomplete"));

        var result = await Ear(engine).TranscribeAsync(new AudioClip(Wav(0.1f), "audio/wav"));

        Assert.False(result.Success);
        Assert.Equal("Couldn't make out that recording just now — please try again.", result.Error);
    }

    [Fact]
    public async Task The_callers_own_cancellation_propagates_rather_than_becoming_a_failure_result()
    {
        // ⚠️ The rule ProviderCancellationSiteTests holds: the household walking away is not a provider
        // error. The inverse — a cancellation from anything else — must NOT propagate, which is why the
        // engine raises its own bound as a TimeoutException (the test above).
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var engine = new FakeMoonshineEngine(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Ear(engine).TranscribeAsync(new AudioClip(Wav(0.1f), "audio/wav"), cts.Token));
    }

    private sealed class FakeMoonshineEngine : IMoonshineEngine
    {
        private readonly string? _text;
        private readonly Exception? _throws;

        public FakeMoonshineEngine(string text) => _text = text;
        public FakeMoonshineEngine(Exception throws) => _throws = throws;

        public bool WasCalled { get; private set; }
        public float[]? LastSamples { get; private set; }
        public int LastSampleRate { get; private set; }

        public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastSamples = samples;
            LastSampleRate = sampleRate;
            if (_throws is not null) throw _throws;
            return Task.FromResult(_text!);
        }

        public void Dispose() { }
    }
}
