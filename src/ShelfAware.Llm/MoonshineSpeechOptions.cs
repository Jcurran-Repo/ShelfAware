namespace ShelfAware.Llm;

/// <summary>Which provider TRANSCRIBES what a person says — the "ear", as distinct from
/// <see cref="SpeechProvider"/>'s mouth. The two are chosen separately on purpose: a box can voice
/// recipes for free while still renting recognition, or the reverse, and pretending one setting covers
/// both would mean a deployment could not do the thing it is mid-way through doing.</summary>
public enum EarProvider
{
    /// <summary>ElevenLabs Scribe (cloud, per-circuit key). The historical default, kept so no existing
    /// deployment changes on upgrade.</summary>
    ElevenLabs,

    /// <summary>Moonshine, run IN THIS PROCESS through sherpa-onnx. $0 per utterance — no key, nothing to
    /// meter, and nothing to deploy beside the app. The ear's half of the same bargain Kokoro made for
    /// the mouth.</summary>
    Moonshine,
}

/// <summary>
/// Configuration for <see cref="MoonshineSpeechToText"/> (bound from the "Speech:Moonshine" section).
///
/// <para>The same shape as <see cref="KokoroSpeechOptions"/>, for the same reason: no URL and no key,
/// because the model runs inside this process. What it needs is a directory of model files on disk —
/// see <c>docs/deploy-moonshine.md</c> for which archive and where to put it.</para>
///
/// <para>Moonshine rather than Whisper, measured rather than assumed (2026-09-21, linux-x64, int8, two
/// threads, pinned to one core to stand in for a 1-vCPU droplet): a 1.5 s spoken command transcribes in
/// 162 ms against Whisper tiny.en's 458 ms, and it heard "sear the chicken" where Whisper heard "see
/// her the chicken". Recognition is far cheaper than synthesis either way — the same box needs ~17 s to
/// SAY a sentence Kokoro-voiced and 0.7 s to HEAR it.</para>
/// </summary>
public class MoonshineSpeechOptions
{
    public const string SectionName = "Speech:Moonshine";

    /// <summary>Directory holding an unpacked sherpa-onnx Moonshine model — the five things
    /// <see cref="MoonshineModelFiles"/> names. Required when <c>Speech:Ear=Moonshine</c>; registration
    /// refuses a directory missing any of them rather than letting the load reach the native library,
    /// which does not throw on a bad path (see <see cref="MoonshineModelFiles"/>).</summary>
    public string ModelDirectory { get; set; } = "";

    /// <summary>Threads the ONNX runtime may use for one transcription. Two, matching Kokoro's default —
    /// though the ear barely needs them: the measurement above is at two threads and stays well under a
    /// fifth of real time even on a single core.</summary>
    public int NumThreads { get; set; } = 2;

    /// <summary>How long one transcription may take in total — the wait behind whatever is already being
    /// transcribed, plus the work itself. ⚠️ There must be a bound, for the reason
    /// <see cref="KokoroSpeechOptions.SynthesisTimeoutSeconds"/> spells out: an in-process model inherits
    /// no HttpClient timeout, and several callers pass no cancellation token. Thirty seconds is roughly
    /// forty times the measured cost of the longest utterance anyone speaks at a microphone, which leaves
    /// room to be queued behind another household and still answer.</summary>
    public int RecognitionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// What is wrong with these settings on their own terms, or null when nothing is. ONE definition,
    /// asked both by registration (so a bad value is a boot failure naming the setting) and by the engine
    /// before it loads (so a value that arrived some other way still can't reach native code) — the same
    /// arrangement as <see cref="KokoroSpeechOptions.Invalid"/>.
    /// </summary>
    public string? Invalid() =>
        string.IsNullOrWhiteSpace(ModelDirectory)
            ? $"{SectionName}:ModelDirectory must name a directory holding an unpacked sherpa-onnx "
              + "Moonshine model. See docs/deploy-moonshine.md."
        : NumThreads < 1
            ? $"{SectionName}:NumThreads is {NumThreads}; it must be at least 1."
        : RecognitionTimeoutSeconds < 1
            ? $"{SectionName}:RecognitionTimeoutSeconds is {RecognitionTimeoutSeconds}; it must be at "
              + "least 1. There is no value meaning 'wait forever' on purpose."
        : null;
}

/// <summary>
/// The five paths that make up an unpacked sherpa-onnx Moonshine model.
///
/// <para>⚠️ ONE definition, asked by both the thing that VALIDATES a model directory at startup and the
/// thing that LOADS it — the same reasoning as <see cref="KokoroModelFiles"/>, and the same native
/// library behind it: a missing file is a line on stderr and a SIGSEGV, not an exception. A validation
/// that checked four of the five would pass, and the app would die on the first spoken word.</para>
///
/// <para>Moonshine is four ONNX files rather than one because it is split into a preprocessor, an
/// encoder, and two decoders (the cached decoder is what makes it fast on short audio — it is the reason
/// this model beats Whisper on exactly the utterances a cook actually speaks).</para>
/// </summary>
/// <param name="Preprocessor">Turns samples into features.</param>
/// <param name="Encoder">The encoder weights.</param>
/// <param name="UncachedDecoder">The decoder's first step.</param>
/// <param name="CachedDecoder">The decoder's subsequent, cached steps.</param>
/// <param name="Tokens">The token table.</param>
public sealed record MoonshineModelFiles(
    string Preprocessor, string Encoder, string UncachedDecoder, string CachedDecoder, string Tokens)
{
    /// <summary>The paths a model directory is expected to hold. The names are fixed by sherpa-onnx's own
    /// packaging of the int8 archive, which is the one worth running on a small box.</summary>
    public static MoonshineModelFiles In(string directory) => new(
        Path.Combine(directory, "preprocess.onnx"),
        Path.Combine(directory, "encode.int8.onnx"),
        Path.Combine(directory, "uncached_decode.int8.onnx"),
        Path.Combine(directory, "cached_decode.int8.onnx"),
        Path.Combine(directory, "tokens.txt"));

    /// <summary>Whichever of the five is not on disk, in the order a person would fix them. Empty means
    /// the directory is loadable as far as anything short of the native library can tell.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (!File.Exists(Preprocessor)) missing.Add(Preprocessor);
        if (!File.Exists(Encoder)) missing.Add(Encoder);
        if (!File.Exists(UncachedDecoder)) missing.Add(UncachedDecoder);
        if (!File.Exists(CachedDecoder)) missing.Add(CachedDecoder);
        if (!File.Exists(Tokens)) missing.Add(Tokens);
        return missing;
    }
}
