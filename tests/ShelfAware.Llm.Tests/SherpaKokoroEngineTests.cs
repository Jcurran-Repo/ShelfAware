using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// ⚠️ The guard that stands between a misconfigured box and a process that dies without saying why.
///
/// <para>sherpa-onnx does not throw on a bad model path. It prints one line to stderr and the process
/// exits with a SIGSEGV — verified against 1.13.8 on Linux: exit 139, no managed exception, no chance for
/// a <c>catch</c> or a log line of ours. So every path into <c>new OfflineTts</c> has to be preceded by a
/// check, and these are the tests for the check, not for the library.</para>
///
/// <para>What the engine does once it HAS a model — that it loads, that it refuses an out-of-range voice,
/// that a cancel actually stops a synthesis mid-run — needs the 150 MB archive and so is not here. It is
/// <c>tools/KokoroCheck</c>, run against a real model on the box it is going to run on. A skipped test
/// that reports green is worse than a tool somebody has to run, because the green stops anyone
/// looking.</para>
/// </summary>
public class SherpaKokoroEngineTests
{
    private static SherpaKokoroEngine Engine(KokoroSpeechOptions options) =>
        new(Options.Create(options), NullLogger<SherpaKokoroEngine>.Instance);

    /// <summary>A directory holding however many of the four model parts are asked for — empty files, since
    /// nothing here gets as far as reading one.</summary>
    private static string ModelDirectory(params string[] parts)
    {
        var directory = Path.Combine(Path.GetTempPath(), "shelfaware-kokoro-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var part in parts)
        {
            if (part == "espeak-ng-data") Directory.CreateDirectory(Path.Combine(directory, part));
            else File.WriteAllBytes(Path.Combine(directory, part), []);
        }
        return directory;
    }

    [Fact]
    public async Task An_unconfigured_model_directory_fails_the_synthesis_rather_than_the_process()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Engine(new KokoroSpeechOptions()).GenerateAsync("hello"));

        Assert.Contains("Speech:Kokoro:ModelDirectory", ex.Message);
    }

    // ⚠️ Each of the four parts, separately. A check that happened to look at only three would pass on a
    // directory missing the fourth, and the process would then die on the first read-aloud — which is
    // exactly the failure this whole guard exists to prevent, arrived at through the guard itself.
    [Theory]
    [InlineData("model.int8.onnx")]
    [InlineData("voices.bin")]
    [InlineData("tokens.txt")]
    [InlineData("espeak-ng-data")]
    public async Task A_model_directory_missing_any_one_part_fails_the_synthesis_rather_than_the_process(string absent)
    {
        string[] complete = ["model.int8.onnx", "voices.bin", "tokens.txt", "espeak-ng-data"];
        var directory = ModelDirectory([.. complete.Where(p => p != absent)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Engine(new KokoroSpeechOptions { ModelDirectory = directory }).GenerateAsync("hello"));

        Assert.Contains(absent, ex.Message);
    }

    // The quantized and full-precision archives name their weights differently, so the check has to follow
    // the setting rather than assume the default — otherwise pointing at a full-precision model would be
    // refused for a file it is right not to have, and a box would be told to fix something that is fine.
    [Fact]
    public async Task The_model_file_that_is_looked_for_follows_the_setting()
    {
        var directory = ModelDirectory("model.onnx", "voices.bin", "tokens.txt", "espeak-ng-data");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(
            new KokoroSpeechOptions { ModelDirectory = directory, ModelFile = "model.int8.onnx" }).GenerateAsync("hello"));
        Assert.Contains("model.int8.onnx", ex.Message);

        // The same directory, asked for the file it actually holds, has nothing missing. (Loading it would
        // then fail inside the native library on an empty ONNX file, which is not this test's business.)
        Assert.Empty(KokoroModelFiles.In(directory, "model.onnx").Missing());
    }

    // ⚠️ The check and the load must read the SAME four paths. They are one definition for exactly that
    // reason: if they ever drifted, the check would pass on a directory the load then died on, and the
    // only symptom would be a process that stops existing.
    [Fact]
    public void The_paths_that_are_checked_are_the_paths_that_are_loaded()
    {
        var files = KokoroModelFiles.In(Path.Combine("models", "kokoro"), "model.int8.onnx");

        Assert.Equal(Path.Combine("models", "kokoro", "model.int8.onnx"), files.Model);
        Assert.Equal(Path.Combine("models", "kokoro", "voices.bin"), files.Voices);
        Assert.Equal(Path.Combine("models", "kokoro", "tokens.txt"), files.Tokens);
        Assert.Equal(Path.Combine("models", "kokoro", "espeak-ng-data"), files.DataDir);
    }

    // ⚠️ A value that cannot mean anything must fail before it reaches native code — and before it takes
    // the synthesis gate, since a division by zero inside the library would take the process with it and
    // leave every queued read waiting on a gate nothing will release.
    [Theory]
    [InlineData("Speed", 0.0)]
    [InlineData("Speed", -1.0)]
    [InlineData("Speed", 99.0)]
    public async Task A_speed_that_cannot_mean_anything_fails_before_the_model_is_touched(string setting, double speed)
    {
        var options = new KokoroSpeechOptions { ModelDirectory = ModelDirectory(), Speed = speed };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(options).GenerateAsync("hello"));

        Assert.Contains(setting, ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task A_thread_count_below_one_is_refused_rather_than_clamped(int threads)
    {
        var options = new KokoroSpeechOptions { ModelDirectory = ModelDirectory(), NumThreads = threads };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(options).GenerateAsync("hello"));

        Assert.Contains("NumThreads", ex.Message);
    }

    // ⚠️ There is deliberately no value meaning "wait forever". The HTTP sidecar this replaced inherited
    // HttpClient's 100-second timeout without anyone choosing it; an in-process call inherits nothing, and
    // several callers pass no cancellation token at all.
    [Fact]
    public async Task There_is_no_way_to_ask_for_an_unbounded_wait()
    {
        var options = new KokoroSpeechOptions { ModelDirectory = ModelDirectory(), SynthesisTimeoutSeconds = 0 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(options).GenerateAsync("hello"));

        Assert.Contains("SynthesisTimeoutSeconds", ex.Message);
    }

    // The settings rule is ONE definition, so what the engine refuses and what registration refuses cannot
    // drift apart — these are the cases both of them are reading.
    [Fact]
    public void Settings_that_are_fine_are_reported_as_fine()
    {
        Assert.Null(new KokoroSpeechOptions { ModelDirectory = "models/kokoro" }.Invalid());
        Assert.Null(new KokoroSpeechOptions { ModelDirectory = "models/kokoro", Speed = 1.0, NumThreads = 1 }.Invalid());
    }

    // A caller that walked away before the model was even asked must see the cancel, not a load.
    [Fact]
    public async Task A_cancelled_caller_is_not_made_to_wait_for_a_model_load()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Engine(new KokoroSpeechOptions { ModelDirectory = ModelDirectory() }).GenerateAsync("hello", cts.Token));
    }

    [Fact]
    public async Task A_disposed_engine_says_so_rather_than_reaching_native_code()
    {
        var engine = Engine(new KokoroSpeechOptions { ModelDirectory = ModelDirectory() });
        engine.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.GenerateAsync("hello"));
    }
}
