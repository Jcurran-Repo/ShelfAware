using SherpaOnnx;
using ShelfAware.Llm;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// The four model families as descriptors: which files each expects, and which block of
/// <see cref="OfflineTtsConfig"/> it fills.
///
/// <para>⚠️ The <c>Apply</c> cases are the discriminating ones, and they are why this file exists. Every
/// family fills the same config object and hands it to the same native constructor, so a descriptor that
/// wrote Piper's paths into the Kokoro block would compile, register, pass every other test in the repo —
/// and then kill the process with a SIGSEGV on the first read-aloud, because sherpa-onnx does not throw
/// on a model it cannot make sense of. Asserting that the OTHER families' blocks are untouched is the
/// half that catches a copy-paste; asserting only that the right one is filled would not.</para>
///
/// <para>⚠️ That isolation check is ONE definition, <see cref="Fills_only_its_own_family_block"/>, over
/// every family — not a hand-written pair of assertions per family. With two families the pairwise
/// version was already two assertions that had to be remembered; with four it would be twelve, and
/// CLAUDE.md is explicit that the way this repo breaks is a rule enforced at call sites one at a time.
/// A fifth family gets a row in the theory, and the row cannot be written without the check.</para>
/// </summary>
public class SherpaTtsModelTests
{
    [Fact]
    public void A_kokoro_directory_expects_four_parts_named_by_the_archive()
    {
        var files = KokoroModelFiles.In("/models/kokoro", "model.int8.onnx");

        Assert.Equal(Path.Combine("/models/kokoro", "model.int8.onnx"), files.Model);
        Assert.Equal(Path.Combine("/models/kokoro", "voices.bin"), files.Voices);
        Assert.Equal(Path.Combine("/models/kokoro", "tokens.txt"), files.Tokens);
        Assert.Equal(Path.Combine("/models/kokoro", "espeak-ng-data"), files.DataDir);
    }

    [Fact]
    public void A_piper_directory_expects_three_because_its_speakers_live_in_the_weights()
    {
        var files = PiperModelFiles.In("/models/piper", "en_US-lessac-medium.onnx");

        Assert.Equal(Path.Combine("/models/piper", "en_US-lessac-medium.onnx"), files.Model);
        Assert.Equal(Path.Combine("/models/piper", "tokens.txt"), files.Tokens);
        Assert.Equal(Path.Combine("/models/piper", "espeak-ng-data"), files.DataDir);
    }

    // The four cases below pin which FIELD each path lands in. That the other families' blocks stay empty
    // is asserted once, for every family at once, by Fills_only_its_own_family_block.

    [Fact]
    public void Kokoro_describes_itself_in_the_kokoro_block()
    {
        var config = new OfflineTtsConfig();

        KokoroModelFiles.In("/models/kokoro", "model.int8.onnx").Apply(ref config);

        Assert.Equal(Path.Combine("/models/kokoro", "model.int8.onnx"), config.Model.Kokoro.Model);
        Assert.Equal(Path.Combine("/models/kokoro", "voices.bin"), config.Model.Kokoro.Voices);
        Assert.Equal(Path.Combine("/models/kokoro", "tokens.txt"), config.Model.Kokoro.Tokens);
        Assert.Equal(Path.Combine("/models/kokoro", "espeak-ng-data"), config.Model.Kokoro.DataDir);
    }

    [Fact]
    public void Piper_describes_itself_in_the_vits_block()
    {
        var config = new OfflineTtsConfig();

        PiperModelFiles.In("/models/piper", "en_US-lessac-medium.onnx").Apply(ref config);

        Assert.Equal(Path.Combine("/models/piper", "en_US-lessac-medium.onnx"), config.Model.Vits.Model);
        Assert.Equal(Path.Combine("/models/piper", "tokens.txt"), config.Model.Vits.Tokens);
        Assert.Equal(Path.Combine("/models/piper", "espeak-ng-data"), config.Model.Vits.DataDir);
    }

    [Fact]
    public void Kitten_describes_itself_in_the_kitten_block()
    {
        var config = new OfflineTtsConfig();

        KittenModelFiles.In("/models/kitten", "model.fp16.onnx").Apply(ref config);

        Assert.Equal(Path.Combine("/models/kitten", "model.fp16.onnx"), config.Model.Kitten.Model);
        Assert.Equal(Path.Combine("/models/kitten", "voices.bin"), config.Model.Kitten.Voices);
        Assert.Equal(Path.Combine("/models/kitten", "tokens.txt"), config.Model.Kitten.Tokens);
        Assert.Equal(Path.Combine("/models/kitten", "espeak-ng-data"), config.Model.Kitten.DataDir);
    }

    /// <summary>⚠️ The acoustic model and the vocoder go in DIFFERENT fields of the same block, and both
    /// are <c>.onnx</c> paths from the same directory, so this is the one mapping a swap would leave
    /// looking entirely reasonable.</summary>
    [Fact]
    public void Matcha_describes_itself_in_the_matcha_block_vocoder_and_voice_apart()
    {
        var config = new OfflineTtsConfig();

        MatchaModelFiles.In("/models/matcha", "model-steps-3.onnx", "vocos-22khz-univ.onnx").Apply(ref config);

        Assert.Equal(Path.Combine("/models/matcha", "model-steps-3.onnx"), config.Model.Matcha.AcousticModel);
        Assert.Equal(Path.Combine("/models/matcha", "vocos-22khz-univ.onnx"), config.Model.Matcha.Vocoder);
        Assert.Equal(Path.Combine("/models/matcha", "tokens.txt"), config.Model.Matcha.Tokens);
        Assert.Equal(Path.Combine("/models/matcha", "espeak-ng-data"), config.Model.Matcha.DataDir);
    }

    [Fact]
    public void A_complete_kokoro_directory_is_missing_nothing()
    {
        using var model = new TempModel("model.int8.onnx", "voices.bin", "tokens.txt");
        model.AddDirectory("espeak-ng-data");

        Assert.Empty(KokoroModelFiles.In(model.Path, "model.int8.onnx").Missing());
    }

    [Fact]
    public void A_complete_piper_directory_is_missing_nothing()
    {
        using var model = new TempModel("en_US-lessac-medium.onnx", "tokens.txt");
        model.AddDirectory("espeak-ng-data");

        Assert.Empty(PiperModelFiles.In(model.Path, "en_US-lessac-medium.onnx").Missing());
    }

    /// <summary>⚠️ The data directory is a DIRECTORY, and a file of that name is not one. Checked because
    /// every family relies on espeak-ng data shipping inside the archive — that is what lets "no
    /// system espeak-ng install" hold — and <c>File.Exists</c> on a directory answers false, so a check
    /// written the obvious way would refuse a model that is perfectly complete.</summary>
    [Fact]
    public void The_espeak_data_is_required_as_a_directory_not_a_file()
    {
        using var model = new TempModel("en_US-lessac-medium.onnx", "tokens.txt", "espeak-ng-data");

        var missing = PiperModelFiles.In(model.Path, "en_US-lessac-medium.onnx").Missing();

        Assert.Contains(missing, m => m.EndsWith("espeak-ng-data", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_names_every_absent_part_not_just_the_first()
    {
        using var model = new TempModel("tokens.txt");

        var missing = PiperModelFiles.In(model.Path, "en_US-lessac-medium.onnx").Missing();

        // The weights and the espeak data, and a person fixing this needs to be told both at once rather
        // than one restart at a time.
        Assert.Equal(2, missing.Count);
    }

    [Fact]
    public void A_kitten_directory_expects_kokoros_four_parts_under_its_own_names()
    {
        var files = KittenModelFiles.In("/models/kitten", "model.fp16.onnx");

        Assert.Equal(Path.Combine("/models/kitten", "model.fp16.onnx"), files.Model);
        Assert.Equal(Path.Combine("/models/kitten", "voices.bin"), files.Voices);
        Assert.Equal(Path.Combine("/models/kitten", "tokens.txt"), files.Tokens);
        Assert.Equal(Path.Combine("/models/kitten", "espeak-ng-data"), files.DataDir);
    }

    /// <summary>⚠️ The acoustic model and the vocoder are both <c>.onnx</c> files in one directory, and
    /// nothing downstream can tell which is which — sherpa-onnx answers a swapped pair the way it answers
    /// every bad input, by dying. So the order they are assembled in is pinned here.</summary>
    [Fact]
    public void A_matcha_directory_expects_a_vocoder_beside_the_acoustic_model()
    {
        var files = MatchaModelFiles.In("/models/matcha", "model-steps-3.onnx", "vocos-22khz-univ.onnx");

        Assert.Equal(Path.Combine("/models/matcha", "model-steps-3.onnx"), files.AcousticModel);
        Assert.Equal(Path.Combine("/models/matcha", "vocos-22khz-univ.onnx"), files.Vocoder);
        Assert.Equal(Path.Combine("/models/matcha", "tokens.txt"), files.Tokens);
        Assert.Equal(Path.Combine("/models/matcha", "espeak-ng-data"), files.DataDir);
    }

    public static TheoryData<string, ISherpaTtsModel> EveryFamily => new()
    {
        { "Kokoro", KokoroModelFiles.In("/models/x", "model.int8.onnx") },
        { "Vits", PiperModelFiles.In("/models/x", "en_US-lessac-medium.onnx") },
        { "Kitten", KittenModelFiles.In("/models/x", "model.fp16.onnx") },
        { "Matcha", MatchaModelFiles.In("/models/x", "model-steps-3.onnx", "vocos-22khz-univ.onnx") },
    };

    /// <summary>
    /// ⚠️ The test this file exists for. Each descriptor must fill its own block of
    /// <see cref="OfflineTtsConfig"/> and leave every other family's block completely empty — checked by
    /// reading the config back through reflection rather than by naming the blocks, so a family added
    /// later is covered by the rule the day it is added rather than the day someone remembers to extend
    /// a list of assertions.
    /// <para>Kitten's file list is Kokoro's exactly, which is what makes this worth the reflection: a
    /// descriptor that filled <c>Kokoro</c> instead of <c>Kitten</c> would be correct in every name it
    /// used and wrong in the only way that matters.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryFamily))]
    public void Fills_only_its_own_family_block(string blockName, ISherpaTtsModel files)
    {
        var config = new OfflineTtsConfig();

        files.Apply(ref config);

        Assert.NotEmpty(PathsIn(config.Model, blockName));
        // PathsIn already drops the empty strings, so an empty result IS an untouched block.
        foreach (var other in FamilyBlockNames.Where(n => n != blockName))
            Assert.Empty(PathsIn(config.Model, other));
    }

    /// <summary>Every model-family block <see cref="OfflineTtsModelConfig"/> carries, read from the type
    /// itself: anything that is not one of the three settings shared by all of them is a family.</summary>
    private static IEnumerable<string> FamilyBlockNames =>
        typeof(OfflineTtsModelConfig).GetFields()
            .Where(f => f.FieldType != typeof(int) && f.FieldType != typeof(string))
            .Select(f => f.Name);

    /// <summary>The non-empty path strings one family block holds.</summary>
    private static IReadOnlyList<string> PathsIn(OfflineTtsModelConfig model, string blockName)
    {
        var block = typeof(OfflineTtsModelConfig).GetField(blockName)?.GetValue(model)
                    ?? throw new InvalidOperationException($"{blockName} is not a block of the model config.");

        return [.. block.GetType().GetFields()
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string?)f.GetValue(block) ?? "")
            .Where(v => v.Length > 0)];
    }

    private sealed class TempModel : IDisposable
    {
        public string Path { get; }

        public TempModel(params string[] files)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "shelfaware-model-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            foreach (var file in files) File.WriteAllBytes(System.IO.Path.Combine(Path, file), []);
        }

        public void AddDirectory(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
