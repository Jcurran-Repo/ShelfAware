using SherpaOnnx;
using ShelfAware.Llm;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// The two model families as descriptors: which files each expects, and which block of
/// <see cref="OfflineTtsConfig"/> it fills.
///
/// <para>⚠️ The <c>Apply</c> cases are the discriminating ones, and they are why this file exists. Both
/// families fill a config object and hand it to the same native constructor, so a descriptor that wrote
/// Piper's paths into the Kokoro block would compile, register, pass every other test in the repo — and
/// then kill the process with a SIGSEGV on the first read-aloud, because sherpa-onnx does not throw on a
/// model it cannot make sense of. Asserting that the OTHER family's block is untouched is the half that
/// catches a copy-paste; asserting only that the right one is filled would not.</para>
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

    [Fact]
    public void Kokoro_describes_itself_in_the_kokoro_block_and_leaves_vits_alone()
    {
        var config = new OfflineTtsConfig();

        KokoroModelFiles.In("/models/kokoro", "model.int8.onnx").Apply(ref config);

        Assert.Equal(Path.Combine("/models/kokoro", "model.int8.onnx"), config.Model.Kokoro.Model);
        Assert.Equal(Path.Combine("/models/kokoro", "voices.bin"), config.Model.Kokoro.Voices);
        // ⚠️ The half that catches a copy-paste: a filled Vits block would make sherpa-onnx load
        // something nobody asked for, and it does not complain about that in a way anything can catch.
        Assert.Empty(config.Model.Vits.Model);
    }

    [Fact]
    public void Piper_describes_itself_in_the_vits_block_and_leaves_kokoro_alone()
    {
        var config = new OfflineTtsConfig();

        PiperModelFiles.In("/models/piper", "en_US-lessac-medium.onnx").Apply(ref config);

        Assert.Equal(Path.Combine("/models/piper", "en_US-lessac-medium.onnx"), config.Model.Vits.Model);
        Assert.Equal(Path.Combine("/models/piper", "tokens.txt"), config.Model.Vits.Tokens);
        Assert.Empty(config.Model.Kokoro.Model);
        // There is no voices file in a Piper archive, so nothing may claim one.
        Assert.Empty(config.Model.Kokoro.Voices);
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
    /// the two families both rely on espeak-ng data shipping inside the archive — that is what lets "no
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
