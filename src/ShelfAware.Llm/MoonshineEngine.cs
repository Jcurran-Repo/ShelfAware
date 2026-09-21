using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace ShelfAware.Llm;

/// <summary>
/// The seam between "what we do with what someone said" and "a neural model on this box" — the ear's
/// counterpart to <see cref="ITtsEngine"/>, and here for the same reason: so
/// <see cref="MoonshineSpeechToText"/>'s decisions (what counts as audio, what the failure copy is,
/// whose cancellation a cancellation was) are testable without a 120 MB model and a native library in
/// the test runner.
/// </summary>
public interface IMoonshineEngine : IDisposable
{
    /// <summary>Transcribes mono <paramref name="samples"/> captured at <paramref name="sampleRate"/>,
    /// loading the model on first use. Returns the text, which may legitimately be empty — a listening
    /// window that caught only a fridge door has nothing to say, and that is an answer, not a failure.</summary>
    /// <exception cref="OperationCanceledException">If <paramref name="cancellationToken"/> is signalled.</exception>
    Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IMoonshineEngine"/> over sherpa-onnx, running Moonshine in this process. No sidecar, no
/// HTTP hop, no Python, no key — the ear's half of the bargain the local mouth made.
///
/// <para><b>Singleton, and it must be</b>, for <see cref="SherpaTtsEngine"/>'s reason: the model is
/// the expensive thing (~1 s to load, a couple of hundred MB resident, most of it the ONNX runtime a
/// local-mouth box has already paid for). Loading it per utterance would cost six times the transcription.</para>
///
/// <para><b>One transcription at a time</b>, on <see cref="_gate"/>. sherpa-onnx does not document
/// <c>OfflineRecognizer</c> as thread-safe, and the gate makes the lazy load race-free without a second
/// lock. Unlike synthesis this is rarely a real queue: a transcription takes a fraction of a second.</para>
///
/// <para><b>Cancellation is honest about what it can do.</b> Decoding is one blocking native call with
/// no progress callback to poll — unlike synthesis, there is no way to stop it part-way. So the token is
/// checked before the call and after it, and the work runs to completion in between. That is acceptable
/// HERE and would not have been for Kokoro: the call is measured in hundreds of milliseconds rather than
/// tens of seconds, so a cancelled caller waits for a blink rather than burning a core on a recipe
/// nobody is listening to. The distinction is written down because "cancellation works" and
/// "cancellation returns promptly" are different claims and only the second one is true of both.</para>
/// </summary>
public sealed class SherpaMoonshineEngine : IMoonshineEngine
{
    private readonly MoonshineSpeechOptions _options;
    private readonly ILogger<SherpaMoonshineEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private int _disposed;

    public SherpaMoonshineEngine(IOptions<MoonshineSpeechOptions> options, ILogger<SherpaMoonshineEngine> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(
        float[] samples, int sampleRate, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_options.Invalid() is { } wrong) throw new InvalidOperationException(wrong);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        var budget = TimeSpan.FromSeconds(_options.RecognitionTimeoutSeconds);

        // Bounded on the WAIT as well as the work, for SherpaKokoroEngine's reason: queued behind another
        // household is just as long a silence as a slow model, with nothing on screen to explain it.
        // False means WE gave up; an exception means the caller left. Different outcomes, kept different.
        if (!await _gate.WaitAsync(budget, cancellationToken)) throw Timeout("waiting for the recognizer");

        try
        {
            ThrowIfDisposed(); // disposal may have begun while this call was queued
            var recognizer = Load();

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(budget);

            // Off the caller's thread (a Blazor circuit): this blocks for the length of the decode. No
            // token on Task.Run on purpose — its token decides only whether the work STARTS, and
            // pretending otherwise is how a cancel that does nothing gets written.
            return await Task.Run(
                () => Hear(recognizer, samples, sampleRate, cancellationToken, bounded.Token),
                CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A bound we chose ran out. ⚠️ Deliberately NOT an <see cref="OperationCanceledException"/>
    /// — that type means the household walked away, and the two must not be told apart by guesswork
    /// upstream.</summary>
    private TimeoutException Timeout(string doing)
    {
        _logger.LogError("Moonshine gave up after {Seconds}s {Doing}.", _options.RecognitionTimeoutSeconds, doing);
        return new TimeoutException($"Moonshine gave up after {_options.RecognitionTimeoutSeconds}s {doing}.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    /// <summary>
    /// Loads the model, once. ⚠️ The file check is not defensive tidiness: the native library answers a
    /// missing model path by printing to stderr and killing the process with a SIGSEGV, so there is no
    /// exception to catch and no log line of ours afterwards. Registration checks the same paths
    /// (<see cref="MoonshineModelFiles"/>) at startup; this is the second reading, for the case where the
    /// files went away between boot and the first spoken word.
    /// </summary>
    private OfflineRecognizer Load()
    {
        if (_recognizer is not null) return _recognizer;

        var files = MoonshineModelFiles.In(_options.ModelDirectory);
        if (files.Missing() is { Count: > 0 } missing)
            throw new InvalidOperationException(
                $"The Moonshine model is incomplete, so it cannot be loaded: {string.Join(", ", missing)} "
                + $"not found. Speech:Moonshine:ModelDirectory is '{_options.ModelDirectory}'. "
                + "See docs/deploy-moonshine.md for the archive to unpack there.");

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Moonshine.Preprocessor = files.Preprocessor;
        config.ModelConfig.Moonshine.Encoder = files.Encoder;
        config.ModelConfig.Moonshine.UncachedDecoder = files.UncachedDecoder;
        config.ModelConfig.Moonshine.CachedDecoder = files.CachedDecoder;
        config.ModelConfig.Tokens = files.Tokens;
        config.ModelConfig.NumThreads = _options.NumThreads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var recognizer = new OfflineRecognizer(config);

        _logger.LogInformation(
            "Loaded Moonshine from {Directory} in {ElapsedMs} ms: {Threads} thread(s).",
            _options.ModelDirectory, started.ElapsedMilliseconds, _options.NumThreads);

        return _recognizer = recognizer;
    }

    /// <summary>
    /// Runs the model. ⚠️ Two tokens, and the difference is the point: <paramref name="caller"/> means
    /// the household walked away, <paramref name="bounded"/> adds "or we ran out of the time we gave
    /// ourselves". They are told apart by ASKING, before anything is thrown — no <c>catch</c> here on
    /// purpose, because a cancellation caught at a provider boundary and rethrown is the defect
    /// <c>ProviderCancellationSiteTests</c> exists to stop.
    /// </summary>
    private string Hear(
        OfflineRecognizer recognizer, float[] samples, int sampleRate,
        CancellationToken caller, CancellationToken bounded)
    {
        bounded.ThrowIfCancellationRequested();

        // ⚠️ Explicitly disposed rather than `using`: OfflineStream has a Dispose() but does not implement
        // IDisposable, so the compiler will not clean it up — and each one holds native memory. The same
        // trap KokoroEngine's generated audio carries.
        var stream = recognizer.CreateStream();
        string text;
        try
        {
            stream.AcceptWaveform(sampleRate, samples);
            recognizer.Decode(stream);
            text = stream.Result.Text ?? "";
        }
        finally
        {
            stream.Dispose();
        }

        // Asked AFTER the decode because there is no way to stop one part-way (see the class remarks):
        // the work is done either way, and the only question left is whether anyone still wants it.
        caller.ThrowIfCancellationRequested();
        if (bounded.IsCancellationRequested) throw Timeout("transcribing");

        return text.Trim();
    }

    /// <summary>
    /// ⚠️ Waits for any transcription in flight before freeing the model. Disposing the native
    /// recognizer out from under a decode running on it is the crash-with-no-log this class is arranged
    /// around, and at host shutdown it is a real race — a circuit closing and the host stopping happen at
    /// the same moment. Bounded by the same timeout every call is, so it cannot hang a shutdown.
    /// <para>The gate is deliberately NOT disposed: <see cref="SemaphoreSlim"/> only holds a disposable
    /// handle once <c>AvailableWaitHandle</c> has been read, which nothing here does, and disposing it
    /// would turn a queued call's <c>finally { Release(); }</c> into a second exception over the first.</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        if (!_gate.Wait(TimeSpan.FromSeconds(_options.RecognitionTimeoutSeconds)))
        {
            _logger.LogWarning("A Moonshine transcription was still running at shutdown; leaving the model loaded.");
            return;
        }

        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
