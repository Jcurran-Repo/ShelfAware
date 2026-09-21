using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Speech;
using ShelfAware.Llm;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Composition of the EAR — the half of <see cref="SpeechRegistration.AddSpeech"/> that chooses whether
/// this box listens with a model of its own or rents the hearing.
///
/// <para>⚠️ This file exists because the branch that added the local ear shipped without it, and the
/// pre-merge review found the gap: every claim below lived only in a doc comment. The sharp one is the
/// startup refusal — the native library answers a missing model file by printing one line to stderr and
/// killing the process with a SIGSEGV, so a directory that is missing a part must be refused HERE, while
/// there is still something able to report it. A check that happened to look at four of the five parts
/// would pass on a directory missing the fifth, and the box would die on the first spoken word: the
/// failure the check exists to prevent, arrived at through the check itself. Hence one case per part.</para>
///
/// <para>The Kokoro twin of these tests is in <see cref="CachingTextToSpeechTests"/>; they are deliberately
/// the same shape, because the mouth and the ear are deliberately the same shape.</para>
/// </summary>
public sealed class MoonshineRegistrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "shelfaware-ear-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Unset_the_ear_is_still_elevenlabs_so_no_deployment_changes_on_upgrade()
    {
        Assert.IsType<ElevenLabsSpeechToText>(EarFor(ear: null));
    }

    [Fact]
    public void Speech_ear_moonshine_selects_the_in_process_model()
    {
        Assert.IsType<MoonshineSpeechToText>(EarFor("Moonshine", AModel()));
    }

    // Case-insensitively, like every other enum-from-config in the app.
    [Fact]
    public void The_ear_setting_is_case_insensitive()
    {
        Assert.IsType<MoonshineSpeechToText>(EarFor("moonshine", AModel()));
    }

    [Fact]
    public void The_mouth_and_the_ear_are_chosen_separately()
    {
        // ⚠️ The whole reason there are two settings: a box can move one before the other, so a
        // deployment is never stuck half-way. A local ear must not drag the mouth along with it.
        using var root = Compose(new()
        {
            ["Speech:Ear"] = "Moonshine",
            ["Speech:Moonshine:ModelDirectory"] = AModel(),
        });
        using var scope = root.CreateScope();

        Assert.IsType<MoonshineSpeechToText>(scope.ServiceProvider.GetRequiredService<ISpeechToText>());
        Assert.StartsWith("elevenlabs", scope.ServiceProvider.GetRequiredService<ITextToSpeech>().OutputFingerprint);
    }

    [Fact]
    public void The_model_is_loaded_once_per_box_not_once_per_transcription()
    {
        // The engine is the expensive thing (~119 MB on disk, a second to load). A transient engine would
        // load it per utterance — which on the 1-vCPU droplet is the whole budget for hearing.
        using var root = Compose(new()
        {
            ["Speech:Ear"] = "Moonshine",
            ["Speech:Moonshine:ModelDirectory"] = AModel(),
        });
        using var scope = root.CreateScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<IMoonshineEngine>(),
            root.CreateScope().ServiceProvider.GetRequiredService<IMoonshineEngine>());
    }

    [Theory]
    [InlineData("preprocess.onnx")]
    [InlineData("encode.int8.onnx")]
    [InlineData("uncached_decode.int8.onnx")]
    [InlineData("cached_decode.int8.onnx")]
    [InlineData("tokens.txt")]
    public void A_model_directory_missing_any_one_part_is_refused_at_registration(string absent)
    {
        var ex = Assert.ThrowsAny<Exception>(() => EarFor("Moonshine", AModel(except: absent)));

        Assert.Contains("Speech:Moonshine:ModelDirectory", DeepMessage(ex));
        Assert.Contains(absent, DeepMessage(ex));
    }

    [Fact]
    public void Choosing_moonshine_without_naming_a_model_is_refused_at_registration()
    {
        var ex = Assert.ThrowsAny<Exception>(() => EarFor("Moonshine"));

        Assert.Contains("Speech:Moonshine:ModelDirectory", DeepMessage(ex));
    }

    [Fact]
    public void A_model_directory_that_is_not_there_at_all_is_refused_at_registration()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            EarFor("Moonshine", Path.Combine(_dir, "models", "nothing-here")));

        Assert.Contains("Speech:Moonshine:ModelDirectory", DeepMessage(ex));
    }

    // A value that cannot mean anything is a boot failure naming the setting, not something handed to
    // native code while holding the recognition gate.
    [Theory]
    [InlineData("Speech:Moonshine:NumThreads", "0")]
    [InlineData("Speech:Moonshine:RecognitionTimeoutSeconds", "0")]
    public void A_setting_that_cannot_mean_anything_is_refused_at_registration(string key, string value)
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            EarFor("Moonshine", AModel(), extra: new() { [key] = value }));

        Assert.Contains(key, DeepMessage(ex));
    }

    /// <summary>A directory shaped like an unpacked Moonshine archive. The files are EMPTY: registration
    /// checks that the five parts are on disk, which is all it can check without loading a model, and
    /// nothing in these tests gets as far as reading one — which is itself the assertion that resolving
    /// the ear does not touch native code.</summary>
    private string AModel(string? except = null)
    {
        // A directory of its own per case, so a test asking for an incomplete model can never be handed
        // one another test already completed.
        var directory = Path.Combine(
            _dir, "models", except ?? "complete", "sherpa-onnx-moonshine-tiny-en-int8");
        Directory.CreateDirectory(directory);

        foreach (var part in new[]
                 {
                     "preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx",
                     "cached_decode.int8.onnx", "tokens.txt",
                 })
            if (part != except) File.WriteAllBytes(Path.Combine(directory, part), []);

        return directory;
    }

    private ISpeechToText EarFor(
        string? ear, string? modelDirectory = null, Dictionary<string, string?>? extra = null)
    {
        var settings = extra ?? [];
        if (ear is not null) settings["Speech:Ear"] = ear;
        if (modelDirectory is not null) settings["Speech:Moonshine:ModelDirectory"] = modelDirectory;

        var root = Compose(settings);
        using var scope = root.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISpeechToText>();
    }

    private ServiceProvider Compose(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IVoiceCredentials>(_ => new StubEarCredentials());
        services.AddScoped<ICurrentHousehold>(_ => new FakeCurrentHousehold());
        services.AddSpeech(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), _dir);
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Options validation surfaces wrapped, so read the whole chain rather than the top.</summary>
    private static string DeepMessage(Exception ex)
    {
        var message = ex.Message;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            message += " | " + inner.Message;
        return message;
    }

    private sealed class StubEarCredentials : IVoiceCredentials
    {
        public string ApiKey => "";
        public string AgentId => "";
    }
}
