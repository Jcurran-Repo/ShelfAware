using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Speech;
using ShelfAware.Llm;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Composition of the Piper mouth, in depth: every part of its model directory and every setting it can
/// get wrong. The rules that are about the SET of families rather than about Piper — which one the
/// provider selects, and that no two can be served each other's cached clips — are in
/// <see cref="LocalVoiceFamilyRegistrationTests"/>, over all four at once.
///
/// <para>Kokoro's half of this lives in <see cref="CachingTextToSpeechTests"/> and these are deliberately
/// its twin, for the reason the ear's tests give: the refusals exist because sherpa-onnx answers a
/// missing model file by printing one line to stderr and killing the process, so an incomplete directory
/// has to be refused HERE, while something can still report it. Hence one case per part.</para>
///
/// <para>⚠️ Each family has its own settings section and a box may carry them all at once (that is the
/// point — switching voices is meant to be a one-line change). That the chosen family is the one that
/// loads, and that its clips are fingerprinted under its own name, is asserted for every family at once
/// in <see cref="LocalVoiceFamilyRegistrationTests"/>. A box that loaded Piper's model and filed its
/// clips as Kokoro's would serve the wrong voice from cache forever, silently, and no green test
/// elsewhere would notice.</para>
/// </summary>
public sealed class PiperRegistrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "shelfaware-piper-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ⚠️ Every case here also proves that resolving the synthesizer does NOT load the model: the files
    // AModel writes are EMPTY, and a real load would die on them (sherpa answers an unreadable model with
    // a SIGSEGV). That property matters because the cache asks for the fingerprint on every lookup,
    // including every HIT — the case that exists to avoid doing work.
    //
    // Three Piper-vs-Kokoro cases used to open this class: that the provider setting selects the
    // in-process model, that a box carrying both sections runs the one the provider NAMES rather than the
    // one it happens to have configured, and the same the other way round. So did a case pinning that the
    // provider is read case-insensitively, and a pair asserting that Piper and Kokoro fingerprint under
    // different names. All five were about the SET of families, and all five are now
    // LocalVoiceFamilyRegistrationTests, over every family at once — six pairs is where writing them out
    // one at a time stops being honest work, and a pair that only covered the two families that existed
    // when it was written is a rule with a hole in it the moment a third arrives.

    [Fact]
    public void The_model_is_loaded_once_per_box_not_once_per_read()
    {
        // The engine is the expensive thing. A transient engine would load the model per read-aloud,
        // which is the whole cost the local voice exists to avoid.
        using var root = Compose(new()
        {
            ["Speech:Provider"] = "Piper",
            ["Speech:Piper:ModelDirectory"] = AModel(),
        });

        Assert.Same(
            root.CreateScope().ServiceProvider.GetRequiredService<ITtsEngine>(),
            root.CreateScope().ServiceProvider.GetRequiredService<ITtsEngine>());
    }

    // ⚠️ One case per part, for the reason in the class comment: a check that happened to look at two of
    // the three would pass on a directory missing the third, and the process would die on the first
    // read-aloud — the failure the check exists to prevent, arrived at through the check itself.
    [Theory]
    [InlineData("en_US-lessac-medium.onnx")]
    [InlineData("tokens.txt")]
    [InlineData("espeak-ng-data")]
    public void A_model_directory_missing_any_one_part_is_refused_at_registration(string absent)
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            FingerprintFor("Piper", modelDirectory: AModel(except: absent)));

        Assert.Contains("Speech:Piper:ModelDirectory", DeepMessage(ex));
        Assert.Contains(absent, DeepMessage(ex));
    }

    /// <summary>⚠️ The case this rule exists for: pointing a box at a different voice is ONE line — the
    /// directory — and the weights inside are found by the archive's own name. Changing voice used to take
    /// two lines, and the second one forgotten was a box that would not start.</summary>
    [Fact]
    public void A_voice_is_found_by_its_directory_alone_with_no_model_file_setting()
    {
        var fingerprint = FingerprintFor("Piper", AModel(voice: "en_US-ryan-high"));

        Assert.Contains("|vits-piper-en_US-ryan-high|en_US-ryan-high.onnx|", fingerprint);
    }

    /// <summary>
    /// ⚠️ Binding a section that names only the directory must leave the model file UNSET. Found by a
    /// probe, not by reasoning: the configuration binder reads a property's current value and writes it
    /// back even when the section has no such key, so while the resolved name had a setter, every bind
    /// came out "explicitly set" — to whatever it resolved to at that instant, which depended on whether
    /// the binder had reached the directory yet. The advice to add the setting went missing, and a binder
    /// that visited the properties in the other order would have frozen a blank name into a box that then
    /// refused to boot. Bound here exactly as registration binds it.
    /// </summary>
    [Fact]
    public void Binding_a_section_that_names_only_the_directory_leaves_the_model_file_unset()
    {
        var bound = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Speech:Piper:ModelDirectory"] = Path.Combine("models", "vits-piper-en_US-ryan-high"),
            })
            .Build()
            .GetSection(PiperSpeechOptions.SectionName)
            .Get<PiperSpeechOptions>()!;

        Assert.False(bound.ModelFileIsSet);
        Assert.Equal("en_US-ryan-high.onnx", bound.ModelFile);
    }

    /// <summary>And the setting still arrives under the key every env file already uses —
    /// <c>Speech__Piper__ModelFile</c> — though the property it lands in is named for what it is.</summary>
    [Fact]
    public void The_model_file_setting_is_still_bound_from_the_ModelFile_key()
    {
        var bound = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Speech:Piper:ModelDirectory"] = Path.Combine("models", "vits-piper-en_US-ryan-high"),
                ["Speech:Piper:ModelFile"] = "custom.onnx",
            })
            .Build()
            .GetSection(PiperSpeechOptions.SectionName)
            .Get<PiperSpeechOptions>()!;

        Assert.True(bound.ModelFileIsSet);
        Assert.Equal("custom.onnx", bound.ModelFile);
    }

    /// <summary>A directory named for one voice but holding another's weights is refused at boot, naming
    /// the file it worked out AND the setting to add — a sentence, where the alternative is a SIGSEGV on
    /// the first read-aloud.</summary>
    [Fact]
    public void A_worked_out_name_that_is_not_on_disk_is_refused_naming_it_and_the_setting()
    {
        var directory = AModel(voice: "en_US-ryan-high");
        File.Move(
            Path.Combine(directory, "en_US-ryan-high.onnx"),
            Path.Combine(directory, "en_US-lessac-medium.onnx"));

        var ex = Assert.ThrowsAny<Exception>(() => FingerprintFor("Piper", directory));

        Assert.Contains("en_US-ryan-high.onnx", DeepMessage(ex));
        Assert.Contains("Speech:Piper:ModelFile", DeepMessage(ex));
    }

    /// <summary>A directory renamed away from sherpa-onnx's <c>vits-piper-&lt;voice&gt;</c> packaging has
    /// no name to work out, and is refused at boot naming the setting that would give it one.</summary>
    [Fact]
    public void A_directory_with_no_voice_in_its_name_is_refused_naming_the_setting()
    {
        var renamed = Path.Combine(_dir, "models", "renamed", "our-voice");
        Directory.CreateDirectory(Path.GetDirectoryName(renamed)!);
        Directory.Move(AModel(), renamed);

        var ex = Assert.ThrowsAny<Exception>(() => FingerprintFor("Piper", renamed));

        Assert.Contains("Speech:Piper:ModelFile", DeepMessage(ex));
        Assert.Contains("'our-voice'", DeepMessage(ex));
    }

    /// <summary>A Piper archive names its weights after the voice, so a box running one whose weights are
    /// named some other way says which — and is refused by the same check when it is wrong, naming the
    /// file it looked for rather than the one it expected.</summary>
    [Fact]
    public void A_model_file_that_is_not_there_is_refused_naming_what_was_looked_for()
    {
        var ex = Assert.ThrowsAny<Exception>(() => FingerprintFor("Piper", AModel(), extra: new()
        {
            ["Speech:Piper:ModelFile"] = "en_US-ryan-high.onnx",
        }));

        Assert.Contains("en_US-ryan-high.onnx", DeepMessage(ex));
    }

    [Fact]
    public void A_named_model_file_that_is_there_is_accepted()
    {
        var directory = AModel();
        File.WriteAllBytes(Path.Combine(directory, "en_US-libritts_r-medium.onnx"), []);

        var fingerprint = FingerprintFor("Piper", directory, extra: new()
        {
            ["Speech:Piper:ModelFile"] = "en_US-libritts_r-medium.onnx",
        });

        // In the fingerprint, because a different set of weights is a different voice: clips made by one
        // must not be served for the other. And the SETTING is what is there, not the name the directory
        // would have given — an explicit ModelFile is the answer, derivation only the fallback.
        Assert.Contains("en_US-libritts_r-medium.onnx", fingerprint);
        Assert.DoesNotContain("en_US-lessac-medium.onnx", fingerprint);
    }

    [Fact]
    public void Choosing_piper_without_naming_a_model_is_refused_at_registration()
    {
        var ex = Assert.ThrowsAny<Exception>(() => FingerprintFor("Piper"));

        Assert.Contains("Speech:Piper:ModelDirectory", DeepMessage(ex));
    }

    // A value that cannot mean anything is a boot failure naming the setting, not something handed to
    // native code while holding the synthesis gate.
    [Theory]
    [InlineData("Speech:Piper:Speed", "0")]
    [InlineData("Speech:Piper:NumThreads", "0")]
    [InlineData("Speech:Piper:SynthesisTimeoutSeconds", "0")]
    public void A_setting_that_cannot_mean_anything_is_refused_at_registration(string key, string value)
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            FingerprintFor("Piper", AModel(), extra: new() { [key] = value }));

        Assert.Contains(key, DeepMessage(ex));
    }

    /// <summary>The refusal must name the PROVIDER that was chosen, not a hard-coded family — otherwise a
    /// Piper misconfiguration would send a person to read the Kokoro documentation.</summary>
    [Fact]
    public void The_refusal_names_the_provider_that_was_chosen()
    {
        var ex = Assert.ThrowsAny<Exception>(() => FingerprintFor("Piper"));

        Assert.Contains("Speech:Provider is Piper", DeepMessage(ex));
    }

    /// <summary>A directory shaped like an unpacked Piper archive. The files are EMPTY: registration
    /// checks that the three parts are on disk, which is all it can check without loading a model, and
    /// nothing in these tests gets as far as reading one — which is itself the assertion that resolving
    /// the voice does not touch native code.</summary>
    private string AModel(string? except = null, string voice = "en_US-lessac-medium")
    {
        // A directory of its own per case, so a test asking for an incomplete model can never be handed
        // one another test already completed. Named as sherpa-onnx packages it, because the weights'
        // name is read off the directory's.
        var directory = Path.Combine(
            _dir, "models", except ?? "complete", PiperSpeechOptions.ArchivePrefix + voice);
        Directory.CreateDirectory(directory);

        foreach (var part in new[] { voice + ".onnx", "tokens.txt" })
            if (part != except) File.WriteAllBytes(Path.Combine(directory, part), []);

        if (except != "espeak-ng-data") Directory.CreateDirectory(Path.Combine(directory, "espeak-ng-data"));

        return directory;
    }

    private string FingerprintFor(
        string? provider, string? modelDirectory = null, Dictionary<string, string?>? extra = null)
    {
        var settings = extra ?? [];
        if (provider is not null) settings["Speech:Provider"] = provider;
        if (modelDirectory is not null) settings["Speech:Piper:ModelDirectory"] = modelDirectory;

        using var root = Compose(settings);
        using var scope = root.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITextToSpeech>().OutputFingerprint;
    }

    private ServiceProvider Compose(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IVoiceCredentials>(_ => new StubPiperCredentials());
        services.AddScoped<ICurrentHousehold>(_ => new FakeCurrentHousehold());
        services.AddSpeech(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), _dir);
        return services.BuildServiceProvider(validateScopes: true);
    }

    // DI may wrap a registration exception in a factory error, so assert against the whole chain.
    private static string DeepMessage(Exception ex)
    {
        var message = ex.Message;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            message += " | " + inner.Message;
        return message;
    }

    private sealed class StubPiperCredentials : IVoiceCredentials
    {
        public string ApiKey => "";
        public string AgentId => "";
    }
}
