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

    /// <summary>Spell numbers, fractions and unit abbreviations out into words (via
    /// <see cref="ShelfAware.Core.Speech.SpeechText"/>) before synthesis. On by default so pronunciation
    /// is consistent with the ElevenLabs path and the cache fingerprint's spelling rules mean the same
    /// thing whichever provider voiced a clip.</summary>
    public bool NormalizeText { get; set; } = true;
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
