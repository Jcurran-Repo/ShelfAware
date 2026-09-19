using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace ShelfAware.Llm;

/// <summary>Raw synthesized audio, as the model produces it: mono float samples and the rate they were
/// produced at. Nothing here knows about containers — <see cref="ShelfAware.Core.Speech.WaveAudio"/>
/// turns this into bytes a browser will play.</summary>
public sealed record KokoroAudio(float[] Samples, int SampleRate);

/// <summary>
/// The seam between "what we say and how we say it" and "a neural model on this box".
///
/// <para>It exists so <see cref="KokoroTextToSpeech"/>'s decisions — what text is actually spoken, what
/// the failure copy is, what the cache fingerprint means, whose cancellation a cancellation was — are
/// testable without a 150 MB model and a native library in the test runner. It is the analogue of the
/// faked <c>HttpMessageHandler</c> that the HTTP-backed providers are tested through.</para>
/// </summary>
public interface IKokoroEngine : IDisposable
{
    /// <summary>Speaks <paramref name="text"/>, loading the model on first use.</summary>
    /// <exception cref="OperationCanceledException">If <paramref name="cancellationToken"/> is signalled —
    /// including part-way through a synthesis already under way.</exception>
    Task<KokoroAudio> GenerateAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IKokoroEngine"/> over sherpa-onnx, running Kokoro-82M in this process. No sidecar, no HTTP
/// hop, no Python, no system espeak-ng — the phonemizer data ships inside the model archive.
///
/// <para><b>Singleton, and it must be.</b> The model is the expensive thing: ~1.6 s to load and a few
/// hundred MB resident. Loading it per request would be slower than the synthesis it enables.</para>
///
/// <para><b>One synthesis at a time.</b> Calls are serialized on <see cref="_gate"/>. sherpa-onnx does not
/// document <c>OfflineTts</c> as thread-safe, and a box small enough to want a free voice is a box where
/// two concurrent syntheses would contend for the same cores anyway. The gate also makes the lazy load
/// race-free without a second lock.</para>
///
/// <para><b>Cancellation is real, not cosmetic.</b> Synthesis is a blocking native call that can run for
/// tens of seconds on a long step, so "await a Task that ignores the token" would leave a reader that the
/// household has closed still burning a core. sherpa's progress callback is polled during generation and
/// returning 0 from it stops the run (verified: a 32 s synthesis stops in 4 s at the first callback), so
/// the token is read there and a cancel actually cancels.</para>
/// </summary>
public sealed class SherpaKokoroEngine : IKokoroEngine
{
    private readonly KokoroSpeechOptions _options;
    private readonly ILogger<SherpaKokoroEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts? _tts;
    private bool _disposed;

    public SherpaKokoroEngine(IOptions<KokoroSpeechOptions> options, ILogger<SherpaKokoroEngine> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<KokoroAudio> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var tts = Load();
            // Off the caller's thread: this blocks for roughly as long as the clip lasts, and the caller is
            // a Blazor circuit. No token here on purpose — Task.Run's token only decides whether the work
            // STARTS, and pretending otherwise is how a cancel that does nothing gets written. The callback
            // inside Speak is what actually stops a synthesis already running.
            return await Task.Run(() => Speak(tts, text, cancellationToken), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the model, once. ⚠️ The file check is not defensive tidiness: the native library answers a
    /// missing model path by printing to stderr and killing the process with a SIGSEGV, so there is no
    /// exception to catch and no log line of ours to find afterwards. Registration checks the same paths
    /// (<see cref="KokoroModelFiles"/>) at startup; this is the second reading, for the case where the
    /// files went away between boot and the first read-aloud.
    /// </summary>
    private OfflineTts Load()
    {
        if (_tts is not null) return _tts;

        var files = KokoroModelFiles.In(_options.ModelDirectory, _options.ModelFile);
        if (files.Missing() is { Count: > 0 } missing)
            throw new InvalidOperationException(
                $"The Kokoro model is incomplete, so it cannot be loaded: {string.Join(", ", missing)} "
                + $"not found. Speech:Kokoro:ModelDirectory is '{_options.ModelDirectory}'. "
                + "See docs/deploy-kokoro.md for the archive to unpack there.");

        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = files.Model;
        config.Model.Kokoro.Voices = files.Voices;
        config.Model.Kokoro.Tokens = files.Tokens;
        config.Model.Kokoro.DataDir = files.DataDir;
        config.Model.NumThreads = Math.Max(1, _options.NumThreads);
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var tts = new OfflineTts(config);

        // ⚠️ Refuse an out-of-range voice rather than letting the model substitute one. It answers an index
        // it doesn't have by quietly using voice 0 — which would leave every cached clip filed under a
        // fingerprint naming a voice that never spoke it, and the cache would go on serving them after the
        // setting was corrected.
        if (_options.SpeakerId < 0 || _options.SpeakerId >= tts.NumSpeakers)
        {
            tts.Dispose();
            throw new InvalidOperationException(
                $"Speech:Kokoro:SpeakerId is {_options.SpeakerId}, but this model has {tts.NumSpeakers} "
                + $"voice(s) — valid values are 0 to {tts.NumSpeakers - 1}.");
        }

        _logger.LogInformation(
            "Loaded Kokoro from {Directory} in {ElapsedMs} ms: {Speakers} voice(s) at {SampleRate} Hz, "
            + "{Threads} thread(s), speaking as voice {SpeakerId}.",
            _options.ModelDirectory, started.ElapsedMilliseconds, tts.NumSpeakers, tts.SampleRate,
            config.Model.NumThreads, _options.SpeakerId);

        return _tts = tts;
    }

    private KokoroAudio Speak(OfflineTts tts, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Returning 0 stops generation. The delegate is handed to native code, so it has to stay reachable
        // for the whole call — hence the local and the KeepAlive below rather than an inline lambda.
        OfflineTtsCallbackProgress onProgress =
            (_, _, _) => cancellationToken.IsCancellationRequested ? 0 : 1;

        var audio = tts.GenerateWithCallbackProgress(text, (float)_options.Speed, _options.SpeakerId, onProgress);
        GC.KeepAlive(onProgress);
        try
        {
            // A stopped run still returns the samples it had reached. They are a fragment of a sentence, so
            // the only honest thing to do with them is throw — the caller asked us to stop.
            cancellationToken.ThrowIfCancellationRequested();
            return new KokoroAudio(audio.Samples, audio.SampleRate);
        }
        finally
        {
            // ⚠️ Explicitly, not with `using`: OfflineTtsGeneratedAudio has a Dispose() but does not
            // implement IDisposable, so it is not something the compiler will clean up for us, and every
            // one of these holds native memory.
            audio.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tts?.Dispose();
        _gate.Dispose();
    }
}
