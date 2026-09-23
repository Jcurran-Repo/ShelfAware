using SherpaOnnx;

namespace ShelfAware.Llm;

/// <summary>Which TTS provider synthesizes read-aloud audio. The EAR is chosen separately by
/// <c>Speech:Ear</c> — see <see cref="EarProvider"/>.</summary>
public enum SpeechProvider
{
    /// <summary>ElevenLabs cloud TTS (per-character cost, per-circuit key). The historical default, kept so
    /// no existing deployment changes on upgrade.</summary>
    ElevenLabs,

    /// <summary>Kokoro-82M, run IN THIS PROCESS through sherpa-onnx. $0 per call — no key, no per-character
    /// fee, nothing to meter, and nothing to deploy beside the app. The warmest of the local voices, and
    /// the slowest: budget 1.3–1.4× real time on a modern core and over 3× on a small shared one.</summary>
    Kokoro,

    /// <summary>A Piper/VITS model, run IN THIS PROCESS through sherpa-onnx, on the same terms as
    /// <see cref="Kokoro"/>. Measured at 0.05× real time where Kokoro takes 1.37× — about 26× faster on
    /// identical hardware, for audio that is noticeably flatter. That trade is what makes a local voice
    /// usable on a small shared CPU, where Kokoro leaves a visitor listening to silence.</summary>
    Piper,

    /// <summary>Matcha-TTS, run IN THIS PROCESS through sherpa-onnx, on the same terms as the others. An
    /// acoustic model plus a separate vocoder rather than one archive — see
    /// <see cref="MatchaSpeechOptions"/>. Added so the choice is not just "the slow warm one or the fast
    /// flat one"; which of the four a given box should run is measured, not assumed, and
    /// <c>docs/voice-bakeoff.md</c> says how.</summary>
    Matcha,

    /// <summary>KittenTTS, run IN THIS PROCESS through sherpa-onnx, on the same terms as the others. A
    /// 24 MB model measured between Piper and Kokoro for speed — 0.24–0.28× real time on a desktop core
    /// against Kokoro's 1.14×, overlapping the faster Piper voices rather than clearing them. See
    /// <see cref="KittenSpeechOptions"/>.</summary>
    Kitten,
}

/// <summary>
/// Configuration for Kokoro-82M (bound from the "Speech:Kokoro" section). The settings themselves, and
/// what is wrong with them, live in <see cref="SherpaTtsOptions"/> — all this adds is which files the
/// Kokoro archive ships and what to call them in a refusal.
///
/// <para>See <c>docs/deploy-kokoro.md</c> for which archive and where to put it.</para>
/// </summary>
public sealed class KokoroSpeechOptions() : SherpaTtsOptions(SectionName, "kokoro", "model.int8.onnx")
{
    public const string SectionName = "Speech:Kokoro";

    /// <inheritdoc />
    protected override string DirectoryHint =>
        "an unpacked sherpa-onnx Kokoro model. See docs/deploy-kokoro.md.";

    /// <inheritdoc />
    /// <remarks>Defaulted to the quantized build because that is the one worth running on a 2 GB box — the
    /// int8 archive is 103 MB against 320 MB, for audio that is hard to tell apart.</remarks>
    protected override string ModelFileHint =>
        "(model.int8.onnx, or model.onnx for the full-precision archive).";

    /// <inheritdoc />
    public override ISherpaTtsModel Model() => KokoroModelFiles.In(ModelDirectory, ModelFile);
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
    : ISherpaTtsModel
{
    /// <summary>The paths a model directory is expected to hold. <paramref name="modelFile"/> varies by
    /// archive (<c>model.int8.onnx</c> vs <c>model.onnx</c>); the other three names are fixed by
    /// sherpa-onnx's own packaging.</summary>
    public static KokoroModelFiles In(string directory, string modelFile) => new(
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
        config.Model.Kokoro.Model = Model;
        config.Model.Kokoro.Voices = Voices;
        config.Model.Kokoro.Tokens = Tokens;
        config.Model.Kokoro.DataDir = DataDir;
    }
}
