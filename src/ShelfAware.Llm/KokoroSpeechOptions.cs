namespace ShelfAware.Llm;

/// <summary>Which TTS provider synthesizes read-aloud audio. The STT ("ear") stays ElevenLabs Scribe —
/// moving speech RECOGNITION off ElevenLabs is a separate seam.</summary>
public enum SpeechProvider
{
    /// <summary>ElevenLabs cloud TTS (per-character cost, per-circuit key). The historical default, kept so
    /// no existing deployment changes on upgrade.</summary>
    ElevenLabs,

    /// <summary>Kokoro-82M, run IN THIS PROCESS through sherpa-onnx. $0 per call — no key, no per-character
    /// fee, nothing to meter, and nothing to deploy beside the app.</summary>
    Kokoro,
}

/// <summary>
/// Configuration for <see cref="KokoroTextToSpeech"/> (bound from the "Speech:Kokoro" section).
///
/// <para>There is no URL and no key here, and that is the point of the shape: the model runs inside the
/// app's own process, so there is no second service to reach, secure, or keep running. What it needs is a
/// directory of model files on disk — see <c>docs/deploy-kokoro.md</c> for which archive and where to put
/// it.</para>
/// </summary>
public class KokoroSpeechOptions
{
    public const string SectionName = "Speech:Kokoro";

    /// <summary>Directory holding an unpacked sherpa-onnx Kokoro model — the four things
    /// <see cref="KokoroModelFiles"/> names. Required when <c>Speech:Provider=Kokoro</c>; registration
    /// refuses a directory missing any of them rather than letting the load reach the native library,
    /// which does not throw on a bad path (see <see cref="KokoroModelFiles"/>).</summary>
    public string ModelDirectory { get; set; } = "";

    /// <summary>The ONNX file's name inside <see cref="ModelDirectory"/>. Defaulted to the quantized build
    /// because that is the one worth running on a 2 GB box — the int8 archive is 103 MB against 320 MB, for
    /// audio that is hard to tell apart. Point it at <c>model.onnx</c> to run the full-precision archive.</summary>
    public string ModelFile { get; set; } = "model.int8.onnx";

    /// <summary>Which of the model's voices speaks, by index. ⚠️ An index, not a name: the archive ships no
    /// name table, so naming voices here would mean carrying a mapping in our source that nothing can check
    /// against the model — and the model answers an out-of-range index by quietly using voice 0, which
    /// would leave the cache fingerprint claiming a voice the audio isn't. Loading validates the index
    /// against the model's own speaker count and refuses rather than substituting. Part of the fingerprint,
    /// so changing it retires clips voiced the old way.</summary>
    public int SpeakerId { get; set; }

    /// <summary>Speaking rate; 1.0 is normal. Defaulted under 1.0 for the same reason as the ElevenLabs
    /// reader — someone cooking with busy hands needs to follow along, not keep up.</summary>
    public double Speed { get; set; } = 0.9;

    /// <summary>Threads the ONNX runtime may use for one synthesis. Two is the default because synthesis is
    /// roughly real-time on a small box and the second thread buys most of what is available (measured on a
    /// 4-core box: 1.39× real time at one thread, 1.08× at two, 0.96× at four), while leaving the rest of
    /// the app room to answer requests. Raise it on a box with cores to spare.</summary>
    public int NumThreads { get; set; } = 2;

    /// <summary>How long one read may take in total — the wait behind whatever is already synthesizing,
    /// plus the synthesis itself. ⚠️ There must be a bound. The HTTP sidecar this replaced inherited
    /// <see cref="HttpClient"/>'s 100-second default and nobody had to think about it; an in-process model
    /// inherits nothing, and several callers (the voice agent, push-to-talk) pass no cancellation token at
    /// all, so without this a household could wait on the queue with no way out. Two minutes is roughly
    /// three times the longest recipe step at the measured rate, which leaves room to be queued behind
    /// another household and still finish.</summary>
    public int SynthesisTimeoutSeconds { get; set; } = 120;

    /// <summary>Spell numbers, fractions and unit abbreviations out into words (via
    /// <see cref="ShelfAware.Core.Speech.SpeechText"/>) before synthesis. On by default so pronunciation
    /// is consistent with the ElevenLabs path and the cache fingerprint's spelling rules mean the same
    /// thing whichever provider voiced a clip.</summary>
    public bool NormalizeText { get; set; } = true;

    /// <summary>
    /// What is wrong with these settings on their own terms, or null when nothing is. ⚠️ ONE definition,
    /// asked both by registration (so a bad value is a boot failure naming the setting) and by the engine
    /// before it loads (so a value that arrived some other way still can't reach native code). Anything
    /// that needs the MODEL to judge — whether the voice index exists — cannot be answered here and is
    /// checked at load; anything that needs the DISK is <see cref="KokoroModelFiles"/>.
    /// </summary>
    public string? Invalid() =>
        string.IsNullOrWhiteSpace(ModelDirectory)
            ? $"{SectionName}:ModelDirectory must name a directory holding an unpacked sherpa-onnx Kokoro "
              + "model. See docs/deploy-kokoro.md."
        : string.IsNullOrWhiteSpace(ModelFile)
            ? $"{SectionName}:ModelFile must name the ONNX file inside that directory "
              + "(model.int8.onnx, or model.onnx for the full-precision archive)."
        // Speed is a divisor on the way to the model's length scale, so zero is not "as fast as possible"
        // — it is a division by zero inside native code, reached while holding the synthesis gate.
        : Speed is <= 0 or > 5
            ? $"{SectionName}:Speed is {Speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}; "
              + "it must be greater than 0 and at most 5. 1.0 is the model's natural pace."
        // ⚠️ Refused rather than clamped up to 1, for the same reason the voice index is: a setting that
        // silently means something other than what it says is a setting nobody can debug from its value.
        : NumThreads < 1
            ? $"{SectionName}:NumThreads is {NumThreads}; it must be at least 1."
        : SynthesisTimeoutSeconds < 1
            ? $"{SectionName}:SynthesisTimeoutSeconds is {SynthesisTimeoutSeconds}; it must be at least 1. "
              + "There is no value meaning 'wait forever' on purpose."
        : null;
}

/// <summary>
/// The four paths that make up an unpacked sherpa-onnx Kokoro model.
///
/// <para>⚠️ ONE definition, asked by both the thing that VALIDATES a model directory at startup and the
/// thing that LOADS it, because the two disagreeing is not a bug that produces an error message. The
/// native library does not throw on a missing file: it prints a line to stderr and the process dies with
/// a SIGSEGV (verified against sherpa-onnx 1.13.8 — exit 139, no managed exception, no chance to catch
/// it). So a validation that checked three of the four paths would pass, and the app would then die on
/// the first read-aloud with nothing in its own logs to say why.</para>
///
/// <para><c>espeak-ng-data</c> is a DIRECTORY and ships inside the model archive — which is what lets the
/// decision "no system espeak-ng install" hold. It is checked as a directory, not a file.</para>
/// </summary>
/// <param name="Model">The ONNX weights.</param>
/// <param name="Voices">The packed voice embeddings.</param>
/// <param name="Tokens">The token table.</param>
/// <param name="DataDir">The bundled espeak-ng data directory used for phonemization.</param>
public sealed record KokoroModelFiles(string Model, string Voices, string Tokens, string DataDir)
{
    /// <summary>The paths a model directory is expected to hold. <paramref name="modelFile"/> varies by
    /// archive (<c>model.int8.onnx</c> vs <c>model.onnx</c>); the other three names are fixed by
    /// sherpa-onnx's own packaging.</summary>
    public static KokoroModelFiles In(string directory, string modelFile) => new(
        Path.Combine(directory, modelFile),
        Path.Combine(directory, "voices.bin"),
        Path.Combine(directory, "tokens.txt"),
        Path.Combine(directory, "espeak-ng-data"));

    /// <summary>Whichever of the four is not on disk, in the order a person would fix them. Empty means the
    /// directory is loadable as far as anything short of the native library can tell.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (!File.Exists(Model)) missing.Add(Model);
        if (!File.Exists(Voices)) missing.Add(Voices);
        if (!File.Exists(Tokens)) missing.Add(Tokens);
        if (!Directory.Exists(DataDir)) missing.Add(DataDir);
        return missing;
    }
}
