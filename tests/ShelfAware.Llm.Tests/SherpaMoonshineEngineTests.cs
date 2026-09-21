using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// ⚠️ The ear's half of the guard that stands between a misconfigured box and a process that dies
/// without saying why — the twin of <see cref="SherpaKokoroEngineTests"/>, deliberately the same shape.
///
/// <para>sherpa-onnx does not throw on a bad model path. It prints one line to stderr and the process
/// exits with a SIGSEGV, so every path into <c>new OfflineRecognizer</c> has to be preceded by a check,
/// and these are the tests for the check, not for the library. Registration checks the same five paths at
/// boot (<c>MoonshineRegistrationTests</c>); this is the second reading, for the case where the files
/// went away between boot and the first spoken word — a model directory on a droplet is one
/// <c>rm -rf</c> or one unlucky deploy away from being empty.</para>
///
/// <para>What the engine does once it HAS a model needs the 119 MB archive and so is not here. It is
/// <c>tools/MoonshineCheck</c>, run against a real model on the box it is going to run on. A skipped test
/// that reports green is worse than a tool somebody has to run, because the green stops anyone
/// looking.</para>
/// </summary>
public class SherpaMoonshineEngineTests
{
    private static SherpaMoonshineEngine Engine(MoonshineSpeechOptions options) =>
        new(Options.Create(options), NullLogger<SherpaMoonshineEngine>.Instance);

    private static float[] Silence => new float[1600];   // a tenth of a second at 16 kHz

    /// <summary>A directory holding however many of the five model parts are asked for — empty files,
    /// since nothing here gets as far as reading one.</summary>
    private static string ModelDirectory(params string[] parts)
    {
        var directory = Path.Combine(Path.GetTempPath(), "shelfaware-moonshine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var part in parts) File.WriteAllBytes(Path.Combine(directory, part), []);
        return directory;
    }

    [Fact]
    public async Task An_unconfigured_model_directory_fails_the_transcription_rather_than_the_process()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Engine(new MoonshineSpeechOptions()).TranscribeAsync(Silence, 16000));

        Assert.Contains("Speech:Moonshine:ModelDirectory", ex.Message);
    }

    // ⚠️ Each of the five parts, separately. A check that happened to look at only four would pass on a
    // directory missing the fifth, and the process would then die on the first spoken word — the failure
    // this whole guard exists to prevent, arrived at through the guard itself.
    [Theory]
    [InlineData("preprocess.onnx")]
    [InlineData("encode.int8.onnx")]
    [InlineData("uncached_decode.int8.onnx")]
    [InlineData("cached_decode.int8.onnx")]
    [InlineData("tokens.txt")]
    public async Task A_model_directory_missing_any_one_part_fails_the_transcription_rather_than_the_process(string absent)
    {
        string[] complete =
        [
            "preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx",
            "cached_decode.int8.onnx", "tokens.txt",
        ];
        var directory = ModelDirectory([.. complete.Where(p => p != absent)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(
            new MoonshineSpeechOptions { ModelDirectory = directory }).TranscribeAsync(Silence, 16000));

        Assert.Contains(absent, ex.Message);
        Assert.Contains("docs/deploy-moonshine.md", ex.Message);   // the message has to be actionable on a box
    }

    // ⚠️ The check and the load must read the SAME five paths. They are one definition for exactly that
    // reason: if they ever drifted, the check would pass on a directory the load then died on, and the
    // only symptom would be a process that stops existing.
    [Fact]
    public void The_paths_that_are_checked_are_the_paths_that_are_loaded()
    {
        var directory = Path.Combine("models", "moonshine");
        var files = MoonshineModelFiles.In(directory);

        Assert.Equal(Path.Combine(directory, "preprocess.onnx"), files.Preprocessor);
        Assert.Equal(Path.Combine(directory, "encode.int8.onnx"), files.Encoder);
        Assert.Equal(Path.Combine(directory, "uncached_decode.int8.onnx"), files.UncachedDecoder);
        Assert.Equal(Path.Combine(directory, "cached_decode.int8.onnx"), files.CachedDecoder);
        Assert.Equal(Path.Combine(directory, "tokens.txt"), files.Tokens);
    }

    // ⚠️ A value that cannot mean anything must fail before it reaches native code — and before it takes
    // the recognition gate, since dying inside the library would leave every queued utterance waiting on
    // a gate nothing will release.
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task A_thread_count_below_one_is_refused_rather_than_clamped(int threads)
    {
        var options = new MoonshineSpeechOptions { ModelDirectory = ModelDirectory(), NumThreads = threads };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Engine(options).TranscribeAsync(Silence, 16000));

        Assert.Contains("NumThreads", ex.Message);
    }

    // ⚠️ There is deliberately no value meaning "wait forever". An in-process call inherits no HttpClient
    // timeout, and several callers pass no cancellation token at all.
    [Fact]
    public async Task There_is_no_way_to_ask_for_an_unbounded_wait()
    {
        var options = new MoonshineSpeechOptions { ModelDirectory = ModelDirectory(), RecognitionTimeoutSeconds = 0 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Engine(options).TranscribeAsync(Silence, 16000));

        Assert.Contains("RecognitionTimeoutSeconds", ex.Message);
    }

    // A sample rate of zero divides into the duration the caller logs, and would reach the native call as
    // a number no capture can have.
    [Theory]
    [InlineData(0)]
    [InlineData(-16000)]
    public async Task A_sample_rate_no_capture_can_have_is_refused_before_the_model_is_touched(int sampleRate)
    {
        var options = new MoonshineSpeechOptions { ModelDirectory = ModelDirectory() };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Engine(options).TranscribeAsync(Silence, sampleRate));
    }

    // The settings rule is ONE definition, so what the engine refuses and what registration refuses cannot
    // drift apart — these are the cases both of them are reading.
    [Fact]
    public void Settings_that_are_fine_are_reported_as_fine()
    {
        Assert.Null(new MoonshineSpeechOptions { ModelDirectory = "models/moonshine" }.Invalid());
        Assert.Null(new MoonshineSpeechOptions
        {
            ModelDirectory = "models/moonshine", NumThreads = 1, RecognitionTimeoutSeconds = 1,
        }.Invalid());
    }

    // A caller that walked away before the model was even asked must see the cancel, not a load.
    [Fact]
    public async Task A_cancelled_caller_is_not_made_to_wait_for_a_model_load()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(
            new MoonshineSpeechOptions { ModelDirectory = ModelDirectory() }).TranscribeAsync(Silence, 16000, cts.Token));
    }

    [Fact]
    public async Task A_disposed_engine_says_so_rather_than_reaching_native_code()
    {
        var engine = Engine(new MoonshineSpeechOptions { ModelDirectory = ModelDirectory() });
        engine.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.TranscribeAsync(Silence, 16000));
    }

    [Fact]
    public void Disposing_twice_is_not_an_error()
    {
        // Host shutdown can race a circuit's own disposal, and the second one must be a no-op rather than
        // a second attempt to free a native recognizer.
        var engine = Engine(new MoonshineSpeechOptions { ModelDirectory = ModelDirectory() });

        engine.Dispose();
        engine.Dispose();
    }
}
