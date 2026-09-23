using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Llm;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// How a Piper voice's weights are named when <c>Speech:Piper:ModelFile</c> is not set: read off the
/// archive's own directory name, <c>vits-piper-&lt;voice&gt;</c> → <c>&lt;voice&gt;.onnx</c>.
///
/// <para>⚠️ The rule exists so a voice change is ONE line on a box. A fixed default was only right for
/// one voice, so changing it would strand every box whose env file named the old voice's directory — the
/// deploy lands, the new default's file is not in that directory, and the app refuses to start. The cases
/// below are the ways that could quietly come back: a derived name the fingerprint does not see, a
/// derivation that overrides an explicit setting, or a guess handed to native code.</para>
/// </summary>
public class PiperModelFileTests
{
    [Theory]
    [InlineData("vits-piper-en_US-ryan-high", "en_US-ryan-high.onnx")]
    [InlineData("vits-piper-en_US-lessac-medium", "en_US-lessac-medium.onnx")]
    [InlineData("vits-piper-en_US-libritts_r-medium", "en_US-libritts_r-medium.onnx")]
    // ⚠️ The quantized builds keep the voice's plain name for their weights — checked against the
    // archives themselves (vits-piper-en_US-ryan-high-int8 unpacks to en_US-ryan-high.onnx).
    [InlineData("vits-piper-en_US-ryan-high-int8", "en_US-ryan-high.onnx")]
    [InlineData("vits-piper-en_US-ryan-medium-fp16", "en_US-ryan-medium.onnx")]
    public void An_unset_model_file_is_worked_out_from_the_archive_name(string archive, string expected)
    {
        var options = Piper(Path.Combine("var", "lib", "shelfaware", "models", archive));

        Assert.Equal(expected, options.ModelFile);
        Assert.False(options.ModelFileIsSet);
        Assert.Null(options.Invalid());
    }

    /// <summary>The same model written with a trailing separator is the same model — the one reading of
    /// the archive name the fingerprint uses already trims it, and the derivation asks that reading.</summary>
    [Fact]
    public void A_trailing_separator_does_not_change_the_derived_name() =>
        Assert.Equal("en_US-ryan-high.onnx",
            Piper(Path.Combine("models", "vits-piper-en_US-ryan-high") + Path.DirectorySeparatorChar).ModelFile);

    /// <summary>Derivation is the FALLBACK. An archive that names its weights some other way is exactly
    /// the case the setting exists for, so a derived name overriding it would make the setting a lie.</summary>
    [Fact]
    public void An_explicit_model_file_wins_over_the_derived_one()
    {
        var options = Piper(Path.Combine("models", "vits-piper-en_US-ryan-high"));
        options.ModelFileSetting = "custom.onnx";

        Assert.Equal("custom.onnx", options.ModelFile);
        Assert.True(options.ModelFileIsSet);
    }

    /// <summary>⚠️ Blank is refused, not read as "work it out". A setting that silently means something
    /// other than what it says is one nobody can debug from its value — the same reason the voice index
    /// is refused rather than substituted.</summary>
    [Fact]
    public void An_explicitly_blank_model_file_is_refused_naming_the_setting()
    {
        var options = Piper(Path.Combine("models", "vits-piper-en_US-ryan-high"));
        options.ModelFileSetting = " ";

        Assert.Contains("Speech:Piper:ModelFile", options.Invalid());
    }

    /// <summary>A directory that does not follow sherpa-onnx's packaging has no answer to read off it, and
    /// must get a sentence naming the setting — never a guess that reaches native code, which answers a bad
    /// path by killing the process.</summary>
    [Theory]
    [InlineData("my-voice")]
    [InlineData("vits-piper-")]            // the prefix and nothing after it names no voice
    [InlineData("vits-piper--int8")]       // nor does a quantization suffix with no voice in front of it
    [InlineData("VITS-PIPER-en_US-ryan-high")] // the packaging is lowercase, and the box's disk is case-sensitive
    public void A_directory_there_is_no_name_to_work_out_from_is_refused_naming_the_setting(string archive)
    {
        var options = Piper(Path.Combine("models", archive));

        Assert.Equal("", options.ModelFile);
        var refusal = options.Invalid();
        Assert.Contains("Speech:Piper:ModelFile", refusal);
        Assert.Contains($"'{archive}'", refusal);
    }

    /// <summary>
    /// ⚠️ The cache-key half, and the reason the derived name is the property rather than something the
    /// load computes on the side: a box that never set <c>ModelFile</c> must file its clips under exactly
    /// the key a box that set it to the same file does. Anything else re-voices every household's
    /// cookbook on the deploy that introduced derivation — on the demo box, where synthesis is the cost.
    /// </summary>
    [Fact]
    public void A_derived_name_fingerprints_exactly_as_the_same_name_set_explicitly()
    {
        var directory = Path.Combine("models", "vits-piper-en_US-lessac-medium");
        var derived = Piper(directory);
        var set = Piper(directory);
        set.ModelFileSetting = "en_US-lessac-medium.onnx";

        Assert.Equal(Fingerprint(set), Fingerprint(derived));
    }

    /// <summary>A quantized build shares its weights' FILE name with the full one, so the archive name is
    /// what keeps their clips apart — they are different audio, however alike the names.</summary>
    [Fact]
    public void A_quantized_build_fingerprints_apart_from_the_full_one_despite_the_same_file_name()
    {
        var full = Piper(Path.Combine("models", "vits-piper-en_US-ryan-high"));
        var int8 = Piper(Path.Combine("models", "vits-piper-en_US-ryan-high-int8"));

        Assert.Equal(full.ModelFile, int8.ModelFile);
        Assert.NotEqual(Fingerprint(full), Fingerprint(int8));
    }

    /// <summary>And two different voices, neither with the setting, must not share a key — the failure a
    /// fingerprint that read the raw (unset) setting would produce, serving one voice's clips for the other.</summary>
    [Fact]
    public void Two_voices_with_no_model_file_set_fingerprint_differently_in_the_weights_part()
    {
        var ryan = Fingerprint(Piper(Path.Combine("models", "vits-piper-en_US-ryan-high"))).Split('|');
        var lessac = Fingerprint(Piper(Path.Combine("models", "vits-piper-en_US-lessac-medium"))).Split('|');

        Assert.Equal("en_US-ryan-high.onnx", ryan[2]);
        Assert.Equal("en_US-lessac-medium.onnx", lessac[2]);
    }

    /// <summary>When a WORKED-OUT name is not on disk, the refusal says both what it looked for and that it
    /// was worked out — with the line to add if the archive names its weights differently.</summary>
    [Fact]
    public void A_derived_name_that_is_not_on_disk_is_described_with_the_setting_to_add()
    {
        var options = Piper(Path.Combine("models", "vits-piper-en_US-ryan-high"));
        var weights = PiperModelFiles.In(options.ModelDirectory, options.ModelFile).Model;

        var described = options.DescribeMissing([weights]);

        Assert.Contains(weights, described);
        Assert.Contains("worked out from the directory's name", described);
        Assert.Contains("Speech:Piper:ModelFile", described);
    }

    /// <summary>A name the operator SET needs no advice to set it, and a missing token table is not the
    /// weights' name being wrong — in both cases the path is the whole story.</summary>
    [Fact]
    public void Advice_to_set_the_model_file_is_given_only_when_the_worked_out_weights_are_missing()
    {
        var directory = Path.Combine("models", "vits-piper-en_US-ryan-high");
        var set = Piper(directory);
        set.ModelFileSetting = "en_US-ryan-high.onnx";
        var tokens = Path.Combine(directory, "tokens.txt");

        Assert.DoesNotContain("Speech:Piper:ModelFile",
            set.DescribeMissing([PiperModelFiles.In(directory, set.ModelFile).Model]));
        Assert.DoesNotContain("Speech:Piper:ModelFile", Piper(directory).DescribeMissing([tokens]));
        Assert.Equal($"{tokens} not found.", Piper(directory).DescribeMissing([tokens]));
    }

    /// <summary>The families whose archives all use one name keep taking it from the constructor —
    /// derivation is Piper's, and nobody else's default moved.</summary>
    [Fact]
    public void A_fixed_name_family_still_defaults_to_its_fixed_name()
    {
        var kokoro = new KokoroSpeechOptions { ModelDirectory = Path.Combine("models", "kokoro-int8-en-v0_19") };

        Assert.Equal("model.int8.onnx", kokoro.ModelFile);
        Assert.False(kokoro.ModelFileIsSet);
    }

    private static PiperSpeechOptions Piper(string directory) => new() { ModelDirectory = directory };

    private static string Fingerprint(SherpaTtsOptions options) => new SherpaTextToSpeech(
        FakeKokoroEngine.Returning([1f]), options, NullLogger<SherpaTextToSpeech>.Instance).OutputFingerprint;
}
