using SherpaOnnx;

namespace ShelfAware.Llm;

/// <summary>
/// One model family's answer to the only two questions that differ between them: which files have to be
/// on disk, and which block of <see cref="OfflineTtsConfig"/> describes them.
///
/// <para>⚠️ This interface is the reason there is one engine rather than one per family. Everything else
/// about running a local voice — the synthesis gate, the timeout that is not a cancellation, the refusal
/// to serve an empty clip, what the cache fingerprint means — is identical for Kokoro and Piper, and a
/// second copy of it would be a second place to fix every bug found in the first. See CLAUDE.md on
/// converting call sites one at a time.</para>
/// </summary>
public interface ISherpaTtsModel
{
    /// <summary>Whichever of this family's paths is not on disk, in the order a person would fix them.
    /// Empty means the directory is loadable as far as anything short of the native library can tell.</summary>
    IReadOnlyList<string> Missing();

    /// <summary>Points <paramref name="config"/> at these files. Each family fills a different sub-block
    /// and leaves the others empty, which is how sherpa-onnx decides what it is loading.
    /// <para>⚠️ <c>ref</c>, and it has to be: every one of sherpa-onnx 1.13.8's config types is a STRUCT
    /// (<c>OfflineTtsConfig</c>, <c>OfflineTtsModelConfig</c> and each family's block alike). Taken by
    /// value, this method would fill a copy, return, and leave the caller's config empty — and an empty
    /// config is not something the native library reports, so the first read-aloud on every box would
    /// die with no managed exception and nothing of ours in the log. Written by value first, and caught
    /// by <c>SherpaTtsModelTests</c> rather than by the suite, which is the argument for the test.</para></summary>
    void Apply(ref OfflineTtsConfig config);
}

/// <summary>
/// What every locally-run voice needs to know, whichever model family it is.
///
/// <para>There is no URL and no key here, and that is the point of the shape: the model runs inside the
/// app's own process, so there is no second service to reach, secure, or keep running. What it needs is a
/// directory of model files on disk.</para>
///
/// <para>Bound from <see cref="Section"/> — <c>Speech:Kokoro</c> or <c>Speech:Piper</c>. The two sections
/// are separate on purpose: a box can carry settings for both and switch between them with
/// <c>Speech:Provider</c> alone, which is what makes "try the fast one, keep the warm one" a one-line
/// change rather than a rewrite of the env file.</para>
/// </summary>
public abstract class SherpaTtsOptions
{
    protected SherpaTtsOptions(string section, string family, string defaultModelFile)
    {
        Section = section;
        Family = family;
        ModelFile = defaultModelFile;
    }

    /// <summary>The configuration section these settings were bound from, used verbatim in every refusal
    /// so a message names the key a person actually has to edit.</summary>
    public string Section { get; }

    /// <summary>This family's name, lowercase, as it appears in the cache fingerprint and the logs.
    /// ⚠️ Part of <see cref="SherpaTextToSpeech.OutputFingerprint"/>, so it cannot change without
    /// retiring every clip the family voiced — which is the correct behaviour, since a different family
    /// is a different voice, but it means the string is data, not a label to tidy up.</summary>
    public string Family { get; }

    /// <summary>Directory holding an unpacked sherpa-onnx model of this family. Required when
    /// <c>Speech:Provider</c> names it; registration refuses a directory missing any expected file rather
    /// than letting the load reach the native library, which does not throw on a bad path.</summary>
    public string ModelDirectory { get; set; } = "";

    /// <summary>The ONNX file's name inside <see cref="ModelDirectory"/>. Defaulted per family, because
    /// the archives name it differently and there is no name that is right for both.</summary>
    public string ModelFile { get; set; }

    /// <summary>Which of the model's voices speaks, by index. ⚠️ An index, not a name: the archives ship
    /// no name table, so naming voices here would mean carrying a mapping in our source that nothing can
    /// check against the model — and the model answers an out-of-range index by quietly using voice 0,
    /// which would leave the cache fingerprint claiming a voice the audio isn't. Loading validates the
    /// index against the model's own speaker count and refuses rather than substituting. Part of the
    /// fingerprint, so changing it retires clips voiced the old way.</summary>
    public int SpeakerId { get; set; }

    /// <summary>Speaking rate; 1.0 is normal. Defaulted under 1.0 for the same reason as the ElevenLabs
    /// reader — someone cooking with busy hands needs to follow along, not keep up.</summary>
    public double Speed { get; set; } = 0.9;

    /// <summary>Threads the ONNX runtime may use for one synthesis. Two is the default because it buys
    /// most of what is available on a small box while leaving the rest of the app room to answer
    /// requests. Raise it on a box with cores to spare.</summary>
    public int NumThreads { get; set; } = 2;

    /// <summary>How long one read may take in total — the wait behind whatever is already synthesizing,
    /// plus the synthesis itself. ⚠️ There must be a bound. An in-process model inherits no timeout from
    /// anything, and several callers (the voice agent, push-to-talk) pass no cancellation token at all,
    /// so without this a household could wait on the queue with no way out.</summary>
    public int SynthesisTimeoutSeconds { get; set; } = 120;

    /// <summary>Spell numbers, fractions and unit abbreviations out into words (via
    /// <see cref="ShelfAware.Core.Speech.SpeechText"/>) before synthesis. On by default so pronunciation
    /// is consistent with the ElevenLabs path and the cache fingerprint's spelling rules mean the same
    /// thing whichever provider voiced a clip.</summary>
    public bool NormalizeText { get; set; } = true;

    /// <summary>What this family's model directory is, in the words a refusal should use.</summary>
    protected abstract string DirectoryHint { get; }

    /// <summary>What this family's ONNX file is called, in the words a refusal should use.</summary>
    protected abstract string ModelFileHint { get; }

    /// <summary>The files this family expects under <see cref="ModelDirectory"/>, and how to describe them
    /// to sherpa-onnx. ⚠️ ONE definition, asked by the thing that VALIDATES a model directory at startup
    /// and by the thing that LOADS it — see <see cref="ISherpaTtsModel"/>.</summary>
    public abstract ISherpaTtsModel Model();

    /// <summary>
    /// What is wrong with these settings on their own terms, or null when nothing is. ⚠️ ONE definition,
    /// asked both by registration (so a bad value is a boot failure naming the setting) and by the engine
    /// before it loads (so a value that arrived some other way still can't reach native code). Anything
    /// that needs the MODEL to judge — whether the voice index exists — cannot be answered here and is
    /// checked at load; anything that needs the DISK is <see cref="Model"/>.
    /// </summary>
    public string? Invalid() =>
        string.IsNullOrWhiteSpace(ModelDirectory)
            ? $"{Section}:ModelDirectory must name a directory holding {DirectoryHint}"
        : string.IsNullOrWhiteSpace(ModelFile)
            ? $"{Section}:ModelFile must name the ONNX file inside that directory {ModelFileHint}"
        // Speed is a divisor on the way to the model's length scale, so zero is not "as fast as possible"
        // — it is a division by zero inside native code, reached while holding the synthesis gate.
        : Speed is <= 0 or > 5
            ? $"{Section}:Speed is {Speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}; "
              + "it must be greater than 0 and at most 5. 1.0 is the model's natural pace."
        // ⚠️ Refused rather than clamped up to 1, for the same reason the voice index is: a setting that
        // silently means something other than what it says is a setting nobody can debug from its value.
        : NumThreads < 1
            ? $"{Section}:NumThreads is {NumThreads}; it must be at least 1."
        : SynthesisTimeoutSeconds < 1
            ? $"{Section}:SynthesisTimeoutSeconds is {SynthesisTimeoutSeconds}; it must be at least 1. "
              + "There is no value meaning 'wait forever' on purpose."
        : null;
}
