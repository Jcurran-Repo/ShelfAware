using SherpaOnnx;

namespace ShelfAware.Llm;

/// <summary>
/// Configuration for a Matcha-TTS voice (bound from the "Speech:Matcha" section). The settings
/// themselves, and what is wrong with them, live in <see cref="SherpaTtsOptions"/> — all this adds is
/// which files a Matcha model needs and what to call them in a refusal.
///
/// <para>⚠️ Matcha is the one family whose model is not a single archive. It is an ACOUSTIC model, which
/// produces a mel spectrogram rather than audio, plus a separate VOCODER that turns that into samples —
/// and the vocoder is published in a different release than the voice, so a directory holding everything
/// the voice archive shipped is still not a model that can speak. That is why <see cref="VocoderFile"/>
/// exists and why a blank one is refused by name: the native library answers a missing vocoder the way it
/// answers every missing file, by killing the process. See <c>docs/voice-bakeoff.md</c>.</para>
/// </summary>
public sealed class MatchaSpeechOptions() : SherpaTtsOptions(SectionName, "matcha", "model-steps-3.onnx")
{
    public const string SectionName = "Speech:Matcha";

    /// <summary>The vocoder ONNX file inside <see cref="SherpaTtsOptions.ModelDirectory"/>. Defaulted to
    /// the universal Vocos build, which is the one sherpa-onnx's own instructions pair with the English
    /// voice; the hifigan builds are alternatives at a different size and quality.
    /// <para>⚠️ In the cache fingerprint, via <see cref="FingerprintExtras"/>. A vocoder is half of what
    /// a Matcha voice SOUNDS like — it is the half that turns a spectrogram into audio — so swapping it
    /// has to retire the clips made with the old one, exactly as swapping the acoustic model does. Left
    /// out, a box that changed vocoder would go on serving the previous one's audio forever, with nothing
    /// anywhere to say why the voice did not change.</para></summary>
    public string VocoderFile { get; set; } = "vocos-22khz-univ.onnx";

    /// <inheritdoc />
    public override IReadOnlyList<string> FingerprintExtras => [VocoderFile];

    /// <inheritdoc />
    protected override string DirectoryHint =>
        "an unpacked sherpa-onnx Matcha model AND its vocoder. See docs/voice-bakeoff.md.";

    /// <inheritdoc />
    protected override string ModelFileHint => "(the acoustic model, e.g. model-steps-3.onnx).";

    /// <inheritdoc />
    /// <remarks>The one rule the shared settings cannot state, because it is the one file no other family
    /// has. Checked here rather than left to <see cref="ISherpaTtsModel.Missing"/> so a blank setting
    /// reads as "you have not named the vocoder" instead of as a missing file whose path is the directory
    /// itself.</remarks>
    protected override string? FamilyInvalid() =>
        string.IsNullOrWhiteSpace(VocoderFile)
            ? $"{Section}:VocoderFile must name the vocoder ONNX file inside that directory "
              + "(e.g. vocos-22khz-univ.onnx). Matcha is an acoustic model: without a vocoder it "
              + "produces a spectrogram, not audio."
            : null;

    /// <inheritdoc />
    public override ISherpaTtsModel Model() => MatchaModelFiles.In(ModelDirectory, ModelFile, VocoderFile);
}

/// <summary>
/// The four paths that make up a loadable Matcha-TTS voice.
///
/// <para>⚠️ ONE definition, for the reason <see cref="KokoroModelFiles"/> gives at length. The hazard is
/// sharper here than for the other families: the acoustic model and the vocoder are both <c>.onnx</c>
/// files in the same directory, so the two are easy to write the wrong way round, and sherpa-onnx does
/// not answer a swapped pair with an exception.</para>
/// </summary>
/// <param name="AcousticModel">The voice — produces a mel spectrogram, not audio.</param>
/// <param name="Vocoder">Turns that spectrogram into samples. Published separately from the voice.</param>
/// <param name="Tokens">The token table.</param>
/// <param name="DataDir">The bundled espeak-ng data directory used for phonemization.</param>
public sealed record MatchaModelFiles(string AcousticModel, string Vocoder, string Tokens, string DataDir)
    : ISherpaTtsModel
{
    /// <summary>The paths a Matcha model directory is expected to hold. Both ONNX names are settable
    /// because neither is fixed: the voice archives name the acoustic model after its step count, and the
    /// vocoder is whichever of the published builds was downloaded beside it.</summary>
    public static MatchaModelFiles In(string directory, string modelFile, string vocoderFile) => new(
        Path.Combine(directory, modelFile),
        Path.Combine(directory, vocoderFile),
        Path.Combine(directory, "tokens.txt"),
        Path.Combine(directory, "espeak-ng-data"));

    /// <inheritdoc />
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (!File.Exists(AcousticModel)) missing.Add(AcousticModel);
        if (!File.Exists(Vocoder)) missing.Add(Vocoder);
        if (!File.Exists(Tokens)) missing.Add(Tokens);
        if (!Directory.Exists(DataDir)) missing.Add(DataDir);
        return missing;
    }

    /// <inheritdoc />
    public void Apply(ref OfflineTtsConfig config)
    {
        config.Model.Matcha.AcousticModel = AcousticModel;
        config.Model.Matcha.Vocoder = Vocoder;
        config.Model.Matcha.Tokens = Tokens;
        config.Model.Matcha.DataDir = DataDir;
    }
}
