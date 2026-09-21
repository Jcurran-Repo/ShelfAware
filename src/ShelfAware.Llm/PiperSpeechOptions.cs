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
public sealed class PiperSpeechOptions() : SherpaTtsOptions(SectionName, "piper", "en_US-lessac-medium.onnx")
{
    public const string SectionName = "Speech:Piper";

    /// <inheritdoc />
    protected override string DirectoryHint =>
        "an unpacked sherpa-onnx Piper/VITS model. See docs/deploy-piper.md.";

    /// <inheritdoc />
    /// <remarks>⚠️ Defaulted to the voice the deploy bootstrap unpacks, not to a name that is right for
    /// every archive — Piper names its weights after the voice (<c>en_US-lessac-medium.onnx</c>,
    /// <c>en_US-libritts_r-medium.onnx</c>), so a box running a different archive must say which.</remarks>
    protected override string ModelFileHint =>
        "(Piper names it after the voice, e.g. en_US-lessac-medium.onnx).";

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
