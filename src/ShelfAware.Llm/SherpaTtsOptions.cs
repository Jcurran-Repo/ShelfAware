using Microsoft.Extensions.Configuration;
using SherpaOnnx;

namespace ShelfAware.Llm;

/// <summary>
/// One model family's answer to the only two questions that differ between them: which files have to be
/// on disk, and which block of <see cref="OfflineTtsConfig"/> describes them.
///
/// <para>⚠️ This interface is the reason there is one engine rather than one per family. Everything else
/// about running a local voice — the synthesis gate, the timeout that is not a cancellation, the refusal
/// to serve an empty clip, what the cache fingerprint means — is identical for every family, and a
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
/// <para>Bound from <see cref="Section"/> — <c>Speech:Kokoro</c>, <c>Speech:Piper</c>,
/// <c>Speech:Kitten</c> or <c>Speech:Matcha</c>. The sections are separate on purpose: a box can carry
/// settings for all of them and switch with <c>Speech:Provider</c> alone, which is what makes "try the
/// fast one, keep the warm one" a one-line change rather than a rewrite of the env file — and what makes
/// a bake-off (docs/voice-bakeoff.md) something an operator can actually run.</para>
/// </summary>
public abstract class SherpaTtsOptions
{
    /// <param name="defaultModelFile">The weights' name when <c>ModelFile</c> is not set, for a family
    /// whose archives all use the same one. Null for a family that works it out instead — see
    /// <see cref="DefaultModelFile"/>.</param>
    protected SherpaTtsOptions(string section, string family, string? defaultModelFile)
    {
        Section = section;
        Family = family;
        _defaultModelFile = defaultModelFile;
    }

    private readonly string? _defaultModelFile;

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

    /// <summary>The <c>ModelFile</c> setting exactly as given (<c>Speech:&lt;Family&gt;:ModelFile</c>), or
    /// null when it was not. Nothing reads this but <see cref="ModelFile"/> — ask that.
    /// <para>⚠️ A property of its own, bound under the <c>ModelFile</c> key, rather than a setter on
    /// <see cref="ModelFile"/>. The configuration binder reads a property's current value and writes it
    /// back even when the section has no such key, so a settable resolved name would come out of every
    /// bind looking EXPLICITLY SET to whatever it resolved to at that moment — which depends on whether
    /// the binder happened to reach <see cref="ModelDirectory"/> first. Measured, not supposed: a probe
    /// bound a directory-only section and got ModelFileIsSet = true.</para></summary>
    [ConfigurationKeyName("ModelFile")]
    public string? ModelFileSetting { get; set; }

    /// <summary>The ONNX file's name inside <see cref="ModelDirectory"/> — the setting when one was given,
    /// otherwise <see cref="DefaultModelFile"/>. Defaulted per family, because the archives name it
    /// differently and there is no name that is right for all of them.
    /// <para>⚠️ The RESOLVED name, and deliberately the only one to read. The fingerprint, the file check
    /// and the load all ask this property, so none of them can see a blank setting while another sees the
    /// name it resolved to — which for Piper would let two boxes running different voices share a cache
    /// key and be served each other's clips. A setting given explicitly wins, blank included: blank is
    /// refused by <see cref="Invalid"/> rather than quietly read as "work it out".</para></summary>
    public string ModelFile => ModelFileSetting ?? DefaultModelFile;

    /// <summary>True when <see cref="ModelFile"/> came from the setting rather than from
    /// <see cref="DefaultModelFile"/> — which decides whether a refusal should tell a person to add it.</summary>
    public bool ModelFileIsSet => ModelFileSetting is not null;

    /// <summary>The weights' name when <c>ModelFile</c> is not set. Most families ship the same name in
    /// every archive and take it from the constructor; Piper names its weights after the voice and
    /// overrides this to read the name off the directory. Blank means there is no answer, which
    /// <see cref="Invalid"/> refuses, naming the setting.</summary>
    protected virtual string DefaultModelFile => _defaultModelFile ?? "";

    /// <summary>The model directory's leaf name, which is the archive's name as sherpa-onnx ships it (e.g.
    /// <c>kokoro-int8-en-v0_19</c>). Trailing separators are trimmed first so <c>/models/kokoro/</c> and
    /// <c>/models/kokoro</c> — the same model, written two ways — cannot disagree.
    /// <para>⚠️ ONE reading of it, asked by the cache fingerprint (where the model's identity is its
    /// archive, not the path it was unpacked to) and by Piper's <see cref="DefaultModelFile"/>. Two
    /// readings would let "which archive is this" have two answers on the same box.</para></summary>
    public string ArchiveName =>
        Path.GetFileName(ModelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

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

    /// <summary>What to tell a person when <see cref="ISherpaTtsModel.Missing"/> of <see cref="Model"/> is
    /// not empty: the paths that were looked for, and anything this family knows about why. ⚠️ ONE
    /// wording, asked by registration, the engine and <c>tools/VoiceCheck</c> alike, so the advice a
    /// person gets does not depend on which of the three found the gap.</summary>
    public string DescribeMissing(IReadOnlyList<string> missing) =>
        MissingAdvice(missing) is { } advice
            ? $"{string.Join(", ", missing)} not found. {advice}"
            : $"{string.Join(", ", missing)} not found.";

    /// <summary>This family's explanation for a gap in its model directory, or null when the paths say
    /// everything there is to say.</summary>
    protected virtual string? MissingAdvice(IReadOnlyList<string> missing) => null;

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
        : FamilyInvalid();

    /// <summary>What is wrong with a setting only THIS family has, or null when it has none. The default
    /// is null because three of the four families are fully described by the settings above.
    /// <para>⚠️ It is the last link of <see cref="Invalid"/> rather than a second method callers must
    /// remember to ask, because "the settings are valid" is one question and two places answering it is
    /// how registration boots a box the engine then refuses to load — the exact shape CLAUDE.md's
    /// one-definition rule is about. Overriding this puts a family's own rule on the same path
    /// registration and the engine already both take.</para></summary>
    protected virtual string? FamilyInvalid() => null;

    /// <summary>Anything else about THIS family that decides how a clip sounds, appended to
    /// <see cref="SherpaTextToSpeech.OutputFingerprint"/>. Empty for a family fully described by the
    /// settings above.
    /// <para>⚠️ Empty means "append nothing", not "append an empty segment" — a family that started
    /// contributing a blank part would change the fingerprint of every clip the other families have
    /// already voiced, and every household's cache would silently re-synthesize from scratch.</para></summary>
    public virtual IReadOnlyList<string> FingerprintExtras => [];
}
