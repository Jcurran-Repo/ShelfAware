using SherpaOnnx;

namespace ShelfAware.Llm;

/// <summary>
/// Configuration for a KittenTTS voice (bound from the "Speech:Kitten" section). The settings themselves,
/// and what is wrong with them, live in <see cref="SherpaTtsOptions"/> — all this adds is which files a
/// Kitten archive ships and what to call them in a refusal.
///
/// <para>Kitten sits between Kokoro and Piper, on a model of 24 MB against Kokoro's 103 MB. Measured
/// rather than taken from a published comparison: on one desktop core, nano ran 0.24–0.28× real time
/// where Kokoro ran 1.14× and Piper's models spread 0.13–0.54× — so it beats Kokoro comfortably and
/// overlaps Piper rather than clearing it, and the <c>mini</c> archive (0.62×) is slower than every
/// Piper in that run. It exists here because the two families we had were the two ends: a voice good
/// enough to ship and too slow for a shared core, and a voice fast enough for anything and noticeably
/// flatter. ⚠️ Those figures are one desktop's; which family a given box should run is a question only
/// <c>tools/VoiceCheck</c> on that box can answer. See <c>docs/voice-bakeoff.md</c>.</para>
/// </summary>
public sealed class KittenSpeechOptions() : SherpaTtsOptions(SectionName, "kitten", "model.fp16.onnx")
{
    public const string SectionName = "Speech:Kitten";

    /// <inheritdoc />
    protected override string DirectoryHint =>
        "an unpacked sherpa-onnx KittenTTS model. See docs/voice-bakeoff.md.";

    /// <inheritdoc />
    /// <remarks>Defaulted to the name every published Kitten archive uses. The archives are fp16 rather
    /// than int8, which is why this is not <c>model.int8.onnx</c> like Kokoro's.</remarks>
    protected override string ModelFileHint => "(model.fp16.onnx in every published Kitten archive).";

    /// <inheritdoc />
    public override ISherpaTtsModel Model() => KittenModelFiles.In(ModelDirectory, ModelFile);
}

/// <summary>
/// The four paths that make up an unpacked sherpa-onnx KittenTTS model.
///
/// <para>⚠️ ONE definition, for the reason <see cref="KokoroModelFiles"/> gives at length: the native
/// library answers a missing file with a SIGSEGV rather than an exception, so a validation that checked
/// three of the four would pass and the app would die on the first read-aloud with nothing of ours in
/// the log.</para>
///
/// <para>The file list is Kokoro's exactly — weights, packed voice embeddings, a token table and the
/// bundled espeak-ng data — but it fills the <c>Kitten</c> block, not the <c>Kokoro</c> one. That
/// resemblance is the whole hazard: a copy-paste here compiles, registers, and passes everything except
/// the run. <c>SherpaTtsModelTests</c> asserts every OTHER family's block stays empty for that reason.</para>
/// </summary>
/// <param name="Model">The ONNX weights.</param>
/// <param name="Voices">The packed voice embeddings — eight in the nano archive, four male and four female.</param>
/// <param name="Tokens">The token table.</param>
/// <param name="DataDir">The bundled espeak-ng data directory used for phonemization.</param>
public sealed record KittenModelFiles(string Model, string Voices, string Tokens, string DataDir)
    : ISherpaTtsModel
{
    /// <summary>The paths a Kitten model directory is expected to hold. <paramref name="modelFile"/> is
    /// settable for the same reason the other families' are — a future archive may name it differently —
    /// and the other three names are fixed by sherpa-onnx's own packaging.</summary>
    public static KittenModelFiles In(string directory, string modelFile) => new(
        Path.Combine(directory, modelFile),
        Path.Combine(directory, "voices.bin"),
        Path.Combine(directory, "tokens.txt"),
        Path.Combine(directory, "espeak-ng-data"));

    /// <inheritdoc />
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (!File.Exists(Model)) missing.Add(Model);
        if (!File.Exists(Voices)) missing.Add(Voices);
        if (!File.Exists(Tokens)) missing.Add(Tokens);
        if (!Directory.Exists(DataDir)) missing.Add(DataDir);
        return missing;
    }

    /// <inheritdoc />
    public void Apply(ref OfflineTtsConfig config)
    {
        config.Model.Kitten.Model = Model;
        config.Model.Kitten.Voices = Voices;
        config.Model.Kitten.Tokens = Tokens;
        config.Model.Kitten.DataDir = DataDir;
    }
}
