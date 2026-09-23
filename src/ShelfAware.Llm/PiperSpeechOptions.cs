using SherpaOnnx;

namespace ShelfAware.Llm;

/// <summary>
/// Configuration for a Piper/VITS voice (bound from the "Speech:Piper" section). The settings themselves,
/// and what is wrong with them, live in <see cref="SherpaTtsOptions"/> — all this adds is which files a
/// Piper archive ships and what to call them in a refusal.
///
/// <para>Piper is the fast local voice: measured at 0.05× real time on two cores where Kokoro takes
/// 1.37×. Below 1× is the threshold that matters, because it is where synthesis outruns playback — a
/// reply can start speaking while the rest of it is still being made, instead of arriving in gaps. See
/// <c>docs/deploy-piper.md</c>.</para>
/// </summary>
public sealed class PiperSpeechOptions() : SherpaTtsOptions(SectionName, "piper", defaultModelFile: null)
{
    public const string SectionName = "Speech:Piper";

    /// <summary>What sherpa-onnx puts in front of the voice's name when it packages a Piper voice:
    /// <c>vits-piper-en_US-ryan-high</c> holds <c>en_US-ryan-high.onnx</c>.</summary>
    public const string ArchivePrefix = "vits-piper-";

    /// <inheritdoc />
    protected override string DirectoryHint =>
        "an unpacked sherpa-onnx Piper/VITS model. See docs/deploy-piper.md.";

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ Worked out from the directory, not fixed. Piper names its weights after the voice, so a fixed
    /// default is only right for one voice — and it used to be, which made every voice change a TWO-line
    /// edit where forgetting the second line was a box that refused to boot. Worse, changing that one
    /// default would have stranded every box whose env file named the old voice's directory: the deploy
    /// lands, the file the new default names is not in the old directory, and the app will not start.
    /// Read off the archive's own name instead, a voice is one line (the directory) on every box, and no
    /// box is stranded by a new voice becoming the recommended one.
    /// <para>The rule is sherpa-onnx's packaging. ⚠️ Including its quantized builds, which keep the
    /// voice's plain name: <c>vits-piper-en_US-ryan-high-int8</c> holds <c>en_US-ryan-high.onnx</c>, not
    /// <c>en_US-ryan-high-int8.onnx</c> — found by unpacking one, not by reading about it. The archive
    /// name still differs, so the cache still tells the two builds apart. A directory that does not
    /// follow the rule — renamed by hand, or a voice packaged some other way — resolves to nothing, and
    /// <see cref="SherpaTtsOptions.Invalid"/> refuses naming the setting rather than letting a guess reach
    /// native code.</para>
    /// </remarks>
    protected override string DefaultModelFile
    {
        get
        {
            if (!ArchiveName.StartsWith(ArchivePrefix, StringComparison.Ordinal)) return "";

            var voice = ArchiveName[ArchivePrefix.Length..];
            if (QuantizedBuildSuffixes.FirstOrDefault(s => voice.EndsWith(s, StringComparison.Ordinal)) is { } suffix)
                voice = voice[..^suffix.Length];

            return voice.Length == 0 ? "" : voice + ".onnx";
        }
    }

    /// <summary>What sherpa-onnx appends to a Piper archive's name for a quantized build of the same voice
    /// — which, unlike the prefix, is NOT carried into the weights' file name.</summary>
    private static readonly string[] QuantizedBuildSuffixes = ["-int8", "-fp16"];

    /// <inheritdoc />
    /// <remarks>Two sentences, because the refusal it completes has two causes: a setting given blank,
    /// or a directory whose name there was nothing to work the weights' name out from.</remarks>
    protected override string ModelFileHint =>
        ModelFileIsSet
            ? "(Piper names it after the voice, e.g. en_US-ryan-high.onnx)."
            : $"— it is worked out from a {ArchivePrefix}<voice> directory name when not set, and "
              + $"'{ArchiveName}' is not one. Set it to the weights' file name (Piper names them after the "
              + "voice, e.g. en_US-ryan-high.onnx).";

    /// <inheritdoc />
    /// <remarks>Only when the name was WORKED OUT and is the thing missing: then the path alone does not
    /// say that it was a guess, or which line to add if the archive names its weights some other way. A
    /// name the operator set needs no advice to set it.</remarks>
    protected override string? MissingAdvice(IReadOnlyList<string> missing) =>
        !ModelFileIsSet && missing.Contains(PiperModelFiles.In(ModelDirectory, ModelFile).Model)
            ? $"{ModelFile} was worked out from the directory's name; if this archive names its weights "
              + $"differently, set {Section}:ModelFile to their file name."
            : null;

    /// <inheritdoc />
    public override ISherpaTtsModel Model() => PiperModelFiles.In(ModelDirectory, ModelFile);
}

/// <summary>
/// The three paths that make up an unpacked sherpa-onnx Piper/VITS model.
///
/// <para>⚠️ ONE definition, for the same reason <see cref="KokoroModelFiles"/> is: the native library
/// answers a missing file with a SIGSEGV rather than an exception, so a validation that checked two of
/// the three would pass and the app would die on the first read-aloud with nothing of ours in the log.</para>
///
/// <para>There is no voices file — a Piper archive carries its speakers inside the ONNX weights, which is
/// why this record has one fewer path than Kokoro's rather than the same four with one left empty.
/// <c>espeak-ng-data</c> ships inside the archive here too, so "no system espeak-ng install" still holds.</para>
/// </summary>
/// <param name="Model">The ONNX weights, named after the voice.</param>
/// <param name="Tokens">The token table.</param>
/// <param name="DataDir">The bundled espeak-ng data directory used for phonemization.</param>
public sealed record PiperModelFiles(string Model, string Tokens, string DataDir) : ISherpaTtsModel
{
    /// <summary>The paths a Piper model directory is expected to hold. <paramref name="modelFile"/> varies
    /// by voice; the other two names are fixed by sherpa-onnx's own packaging.</summary>
    public static PiperModelFiles In(string directory, string modelFile) => new(
        Path.Combine(directory, modelFile),
        Path.Combine(directory, "tokens.txt"),
        Path.Combine(directory, "espeak-ng-data"));

    /// <inheritdoc />
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (!File.Exists(Model)) missing.Add(Model);
        if (!File.Exists(Tokens)) missing.Add(Tokens);
        if (!Directory.Exists(DataDir)) missing.Add(DataDir);
        return missing;
    }

    /// <inheritdoc />
    public void Apply(ref OfflineTtsConfig config)
    {
        config.Model.Vits.Model = Model;
        config.Model.Vits.Tokens = Tokens;
        config.Model.Vits.DataDir = DataDir;
    }
}
