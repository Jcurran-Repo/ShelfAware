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
/// <para>⚠️ The cases that are NOT a twin are the ones that matter most: the two families each have their
/// own settings section, and a box may carry both at once (that is the point — switching voices is meant
/// to be a one-line change). So there are tests that the chosen family is the one that loads, and that
/// its clips are fingerprinted under its own name. A box that loaded Piper's model and filed its clips as
/// Kokoro's would serve the wrong voice from cache forever, silently, and no green test elsewhere would
/// notice.</para>
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
    [Fact]
    public void Speech_provider_piper_selects_the_in_process_model()
    {
        Assert.StartsWith("piper", FingerprintFor(provider: "Piper", modelDirectory: AModel()));
    }

    // Case-insensitively, like every other enum-from-config in the app.
    [Fact]
    public void The_provider_setting_is_case_insensitive()
    {
        Assert.StartsWith("piper", FingerprintFor(provider: "piper", modelDirectory: AModel()));
    }

    /// <summary>⚠️ The sharp one. Both sections configured, and the provider decides — not the presence of
    /// a section. A box moving from Kokoro to Piper keeps its old settings in the env file (that is how a
    /// person rolls back), so "Kokoro is configured" must not mean "Kokoro is running".</summary>
    [Fact]
    public void A_box_carrying_both_sections_runs_the_one_the_provider_names()
    {
        var fingerprint = FingerprintFor("Piper", AModel(), extra: new()
        {
            ["Speech:Kokoro:ModelDirectory"] = AKokoroModel(),
        });

        Assert.StartsWith("piper", fingerprint);
    }

    /// <summary>The same, the other way round — because a rule that only holds in one direction is half a
    /// rule, and this is the direction the family box takes.</summary>
    [Fact]
    public void Kokoro_still_wins_when_both_sections_are_present_and_it_is_named()
    {
        var fingerprint = FingerprintFor("Kokoro", extra: new()
        {
            ["Speech:Kokoro:ModelDirectory"] = AKokoroModel(),
            ["Speech:Piper:ModelDirectory"] = AModel(),
        });

        Assert.StartsWith("kokoro", fingerprint);
    }

    // ⚠️ "No two families share a fingerprint prefix" lived here as a Kokoro-vs-Piper pair until Matcha
    // and Kitten arrived. It is now LocalVoiceFamilyRegistrationTests.No_two_families_fingerprint_the_same,
    // over every family at once — six pairs is where writing them out stops being honest work.

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

    /// <summary>A Piper archive names its weights after the voice, so a box running something other than
    /// the bootstrapped default says which — and is refused by the same check when it is wrong, naming
    /// the file it looked for rather than the one it expected.</summary>
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
        // must not be served for the other.
        Assert.Contains("en_US-libritts_r-medium.onnx", fingerprint);
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
    private string AModel(string? except = null)
    {
        // A directory of its own per case, so a test asking for an incomplete model can never be handed
        // one another test already completed.
        var directory = Path.Combine(
            _dir, "models", except ?? "complete", "vits-piper-en_US-lessac-medium");
        Directory.CreateDirectory(directory);

        foreach (var part in new[] { "en_US-lessac-medium.onnx", "tokens.txt" })
            if (part != except) File.WriteAllBytes(Path.Combine(directory, part), []);

        if (except != "espeak-ng-data") Directory.CreateDirectory(Path.Combine(directory, "espeak-ng-data"));

        return directory;
    }

    /// <summary>The Kokoro twin of <see cref="AModel"/>, for the cases that configure both families.</summary>
    private string AKokoroModel()
    {
        var directory = Path.Combine(_dir, "models", "kokoro", "kokoro-int8-en-v0_19");
        Directory.CreateDirectory(directory);

        foreach (var part in new[] { "model.int8.onnx", "voices.bin", "tokens.txt" })
            File.WriteAllBytes(Path.Combine(directory, part), []);

        Directory.CreateDirectory(Path.Combine(directory, "espeak-ng-data"));
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
