using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace ShelfAware.Llm;

/// <summary>Raw synthesized audio, as the model produces it: mono float samples and the rate they were
/// produced at. Nothing here knows about containers — <see cref="ShelfAware.Core.Speech.WaveAudio"/>
/// turns this into bytes a browser will play.</summary>
public sealed record SynthesizedAudio(float[] Samples, int SampleRate);

/// <summary>
/// The seam between "what we say and how we say it" and "a neural model on this box".
///
/// <para>It exists so <see cref="SherpaTextToSpeech"/>'s decisions — what text is actually spoken, what
/// the failure copy is, what the cache fingerprint means, whose cancellation a cancellation was — are
/// testable without a 150 MB model and a native library in the test runner. It is the analogue of the
/// faked <c>HttpMessageHandler</c> that the HTTP-backed providers are tested through.</para>
/// </summary>
public interface ITtsEngine : IDisposable
{
    /// <summary>Speaks <paramref name="text"/>, loading the model on first use.</summary>
    /// <exception cref="OperationCanceledException">If <paramref name="cancellationToken"/> is signalled —
    /// including part-way through a synthesis already under way.</exception>
    Task<SynthesizedAudio> GenerateAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="ITtsEngine"/> over sherpa-onnx, running a local voice in this process. No sidecar, no HTTP
/// hop, no Python, no system espeak-ng — the phonemizer data ships inside the model archive.
///
/// <para><b>One engine, every family.</b> Kokoro and Piper differ in exactly two things — which files
/// must exist, and which block of <see cref="OfflineTtsConfig"/> names them — and both live behind
/// <see cref="ISherpaTtsModel"/>. Everything below is identical for either, which is why there is no
/// second copy of it to drift out of step. A third family (a cloned voice, say) is a descriptor.</para>
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
public sealed class SherpaTtsEngine : ITtsEngine
{
    private readonly SherpaTtsOptions _options;
    private readonly ILogger<SherpaTtsEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineTts? _tts;
    private int _disposed;

    /// <param name="options">The chosen family's settings. ⚠️ Taken as the bound object rather than
    /// <c>IOptions&lt;T&gt;</c> because the family is decided once, at registration, by reading
    /// <c>Speech:Provider</c> — an engine that resolved its own options would have to know which of the
    /// two sections to ask for, which is the decision registration has already made.</param>
    public SherpaTtsEngine(SherpaTtsOptions options, ILogger<SherpaTtsEngine> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<SynthesizedAudio> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_options.Invalid() is { } wrong) throw new InvalidOperationException(wrong);

        var budget = TimeSpan.FromSeconds(_options.SynthesisTimeoutSeconds);

        // ⚠️ The bound is on the WAIT as well as on the synthesis, because being queued behind another
        // household's narration is just as long a silence as a slow model is, and neither has anything on
        // screen to explain it. WaitAsync's own timeout is used rather than a cancelling token so that the
        // two outcomes stay different things: false means WE gave up, an exception means the caller left.
        if (!await _gate.WaitAsync(budget, cancellationToken))
            throw Timeout("waiting for the synthesizer");

        try
        {
            // Disposal may have started while this call was queued, in which case the model is gone.
            ThrowIfDisposed();
            var tts = Load();

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(budget);

            // Off the caller's thread: this blocks for roughly as long as the clip lasts, and the caller is
            // a Blazor circuit. No token on Task.Run on purpose — its token only decides whether the work
            // STARTS, and pretending otherwise is how a cancel that does nothing gets written. The callback
            // inside Speak is what actually stops a synthesis already running.
            return await Task.Run(() => Speak(tts, text, cancellationToken, bounded.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A bound we chose ran out. ⚠️ Deliberately NOT an <see cref="OperationCanceledException"/>:
    /// that type means the household walked away, and a service that reports its own slowness in the
    /// caller's words is how a provider failure ends up thrown out through a Blazor event handler instead
    /// of being failed softly.</summary>
    private TimeoutException Timeout(string doing)
    {
        _logger.LogError("{Family} gave up after {Seconds}s {Doing}.",
            _options.Family, _options.SynthesisTimeoutSeconds, doing);
        return new TimeoutException(
            $"{_options.Family} gave up after {_options.SynthesisTimeoutSeconds}s {doing}.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    /// <summary>
    /// Loads the model, once. ⚠️ The file check is not defensive tidiness: the native library answers a
    /// missing model path by printing to stderr and killing the process with a SIGSEGV, so there is no
    /// exception to catch and no log line of ours to find afterwards. Registration checks the same paths
    /// (<see cref="ISherpaTtsModel"/>) at startup; this is the second reading, for the case where the
    /// files went away between boot and the first read-aloud.
    /// </summary>
    private OfflineTts Load()
    {
        if (_tts is not null) return _tts;

        var files = _options.Model();
        if (files.Missing() is { Count: > 0 } missing)
            throw new InvalidOperationException(
                $"The {_options.Family} model is incomplete, so it cannot be loaded: "
                + $"{string.Join(", ", missing)} not found. {_options.Section}:ModelDirectory is "
                + $"'{_options.ModelDirectory}'. See docs/ for the archive to unpack there.");

        var config = new OfflineTtsConfig();
        files.Apply(ref config);
        config.Model.NumThreads = _options.NumThreads;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var tts = new OfflineTts(config);

        // ⚠️ Read the model's facts into locals BEFORE anything can dispose it. These properties read
        // through the native handle, so asking one after Dispose is a use-after-free — which does not
        // throw, it takes the process out with a SIGSEGV, and it cost an hour here: the refusal below
        // interpolated tts.NumSpeakers into its own message one line after disposing tts, so the guard
        // fired correctly and then crashed on the way to saying so. Every test still passed; only running
        // it found it.
        var speakers = tts.NumSpeakers;
        var sampleRate = tts.SampleRate;

        // ⚠️ Refuse an out-of-range voice rather than letting the model substitute one. It answers an index
        // it doesn't have by quietly using voice 0 — which would leave every cached clip filed under a
        // fingerprint naming a voice that never spoke it, and the cache would go on serving them after the
        // setting was corrected.
        if (_options.SpeakerId < 0 || _options.SpeakerId >= speakers)
        {
            tts.Dispose();
            throw new InvalidOperationException(
                $"{_options.Section}:SpeakerId is {_options.SpeakerId}, but this model has {speakers} "
                + $"voice(s) — valid values are 0 to {speakers - 1}.");
        }

        _logger.LogInformation(
            "Loaded {Family} from {Directory} in {ElapsedMs} ms: {Speakers} voice(s) at {SampleRate} Hz, "
            + "{Threads} thread(s), speaking as voice {SpeakerId}.",
            _options.Family, _options.ModelDirectory, started.ElapsedMilliseconds, speakers, sampleRate,
            config.Model.NumThreads, _options.SpeakerId);

        return _tts = tts;
    }

    /// <summary>
    /// Runs the model. ⚠️ Two tokens, and the difference between them is the whole point: <paramref
    /// name="caller"/> means the household walked away, <paramref name="bounded"/> adds "or we ran out of
    /// the time we gave ourselves". Both stop the run; only the first is a cancellation. They are told
    /// apart by ASKING, before anything is thrown — there is no <c>catch</c> here on purpose, because a
    /// cancellation caught at a provider boundary and rethrown is exactly the defect
    /// <c>ProviderCancellationSiteTests</c> exists to stop, and a filter reading the inverse of that rule
    /// is the same defect wearing the rule's clothes.
    /// </summary>
    private SynthesizedAudio Speak(OfflineTts tts, string text, CancellationToken caller, CancellationToken bounded)
    {
        bounded.ThrowIfCancellationRequested();

        // Returning 0 stops generation. The delegate is handed to native code, so it has to stay reachable
        // for the whole call — hence the local and the KeepAlive below rather than an inline lambda.
        OfflineTtsCallbackProgress onProgress = (_, _, _) => bounded.IsCancellationRequested ? 0 : 1;

        var audio = tts.GenerateWithCallbackProgress(text, (float)_options.Speed, _options.SpeakerId, onProgress);
        GC.KeepAlive(onProgress);
        try
        {
            // A stopped run still returns the samples it had reached. They are a fragment of a sentence, so
            // the only honest thing to do with them is throw — nobody is waiting for half a step.
            caller.ThrowIfCancellationRequested();
            if (bounded.IsCancellationRequested) throw Timeout("synthesizing");

            return new SynthesizedAudio(audio.Samples, audio.SampleRate);
        }
        finally
        {
            // ⚠️ Explicitly, not with `using`: OfflineTtsGeneratedAudio has a Dispose() but does not
            // implement IDisposable, so it is not something the compiler will clean up for us, and every
            // one of these holds native memory.
            audio.Dispose();
        }
    }

    /// <summary>
    /// ⚠️ Waits for any synthesis in flight before freeing the model. Disposing the native
    /// <c>OfflineTts</c> out from under a <c>GenerateWithCallbackProgress</c> already running on it is
    /// precisely the crash-with-no-log this whole class is arranged around — and at host shutdown it is a
    /// real race, because a circuit closing and the host stopping happen at the same moment. The wait is
    /// bounded by the same timeout every call is, so it cannot hang a shutdown indefinitely.
    /// <para>The gate itself is deliberately NOT disposed. <see cref="SemaphoreSlim"/> only holds a
    /// disposable handle once <c>AvailableWaitHandle</c> has been read, which nothing here does — and
    /// disposing it would turn a queued call's <c>finally { Release(); }</c> into a second exception
    /// thrown over the first.</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        if (!_gate.Wait(TimeSpan.FromSeconds(_options.SynthesisTimeoutSeconds)))
        {
            // Freeing it anyway would segfault the process on its way out, which looks like a crash rather
            // than a shutdown. Leaving it is a leak in a process that is ending.
            _logger.LogWarning("A {Family} synthesis was still running at shutdown; leaving the model "
                + "loaded.", _options.Family);
            return;
        }

        try
        {
            _tts?.Dispose();
            _tts = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
