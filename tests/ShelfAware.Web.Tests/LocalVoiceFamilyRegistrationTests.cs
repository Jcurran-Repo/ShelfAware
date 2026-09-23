using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Speech;
using ShelfAware.Llm;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The rules that must hold for EVERY local voice family, asked of all of them at once.
///
/// <para><see cref="PiperRegistrationTests"/> covers one family in depth — every part of its directory,
/// every setting it can get wrong. This file covers the properties that are about the SET: that the
/// provider setting picks the family it names, that each family is refused by its own section's name, and
/// that no two families can be served each other's cached clips.</para>
///
/// <para>⚠️ It is a theory over a list of families rather than four copies of the same facts, because the
/// failure this repo keeps paying for is a rule applied at call sites one at a time (CLAUDE.md). When
/// Matcha and Kitten were added there were two families and the pairwise fingerprint test was already
/// showing the strain — four families make six pairs, and the fifth would make ten. Adding a family here
/// means adding a row, and a row that is wrong fails rather than being quietly uncovered.</para>
/// </summary>
public sealed class LocalVoiceFamilyRegistrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "shelfaware-voice-family-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Every family that runs a model in this process, with the section it binds from and the
    /// prefix its clips are fingerprinted under. ElevenLabs is deliberately absent: it is the one mouth
    /// that is not a model on disk, so none of the rules below are about it.</summary>
    public static TheoryData<string, string, string> EveryLocalFamily => new()
    {
        { "Kokoro", "Speech:Kokoro", "kokoro" },
        { "Piper", "Speech:Piper", "piper" },
        { "Matcha", "Speech:Matcha", "matcha" },
        { "Kitten", "Speech:Kitten", "kitten" },
    };

    [Theory]
    [MemberData(nameof(EveryLocalFamily))]
    public void The_provider_setting_selects_the_family_it_names(string provider, string _, string prefix)
    {
        Assert.StartsWith(prefix, FingerprintFor(provider));
    }

    /// <summary>Case-insensitively, like every other enum-from-config in the app — a box's env file is
    /// typed by a person.</summary>
    [Theory]
    [MemberData(nameof(EveryLocalFamily))]
    public void The_provider_setting_is_case_insensitive(string provider, string _, string prefix)
    {
        Assert.StartsWith(prefix, FingerprintFor(provider.ToLowerInvariant()));
    }

    /// <summary>⚠️ A box may carry every family's settings at once — that is the point of separate
    /// sections, and it is how a person rolls a voice change back. So the PROVIDER decides, never the
    /// presence of a section: with all four configured, each in turn must be the one that loads.</summary>
    [Theory]
    [MemberData(nameof(EveryLocalFamily))]
    public void A_box_carrying_every_section_runs_the_one_the_provider_names(
        string provider, string _, string prefix)
    {
        Assert.StartsWith(prefix, FingerprintFor(provider, configureEveryFamily: true));
    }

    /// <summary>
    /// ⚠️ No two families may share a fingerprint PREFIX. A household that switched voices would
    /// otherwise be served its old clips forever — same cache key, different voice, and no error anywhere
    /// to say so.
    /// <para>The prefix, not the whole string, and that distinction is the test. Every family here also
    /// has a different archive name and a different weights filename, so comparing whole fingerprints
    /// would pass even if two families reported the same <c>Family</c> — which is exactly the
    /// copy-paste this is meant to catch, since a new family's options class starts life as a copy of
    /// an existing one.</para>
    /// </summary>
    [Fact]
    public void No_two_families_fingerprint_under_the_same_name()
    {
        var prefixes = Families.Select(f => FingerprintFor(f.Family).Split('|')[0]).ToList();

        Assert.Equal(prefixes.Count, prefixes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// ⚠️ The table this file is driven from must name every local family EXACTLY once — checked
    /// against <see cref="SpeechProvider"/> itself rather than against a number typed here.
    ///
    /// <para>This is the reach guard for every theory above, and it is the one that matters: a theory
    /// whose table is missing a row does not fail, it passes having tested one family fewer, and the
    /// family it skipped is always the one just added. Adding a member to the enum without adding its
    /// row here is the whole failure, so the enum is what it is compared to. A duplicate row is the
    /// other half — it tests one family twice and reports four rows for three families.</para>
    /// </summary>
    [Fact]
    public void The_family_table_names_every_local_family_exactly_once()
    {
        // ElevenLabs is the one mouth that is not a model on disk, so it is the one member with no row.
        var local = Enum.GetNames<SpeechProvider>().Where(n => n != nameof(SpeechProvider.ElevenLabs));

        Assert.Equal(local.Order(), Families.Select(f => f.Family).Order());
        Assert.Equal(
            Families.Count(),
            Families.Select(f => f.Section).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Choosing a family without telling it where its model is must name that family's own
    /// setting — a Matcha misconfiguration that sent a person to the Kokoro section would be worse than
    /// no message at all.</summary>
    [Theory]
    [MemberData(nameof(EveryLocalFamily))]
    public void A_family_without_a_model_directory_is_refused_naming_its_own_section(
        string provider, string section, string _)
    {
        var ex = Assert.ThrowsAny<Exception>(() => FingerprintFor(provider, withModels: false));

        Assert.Contains($"{section}:ModelDirectory", DeepMessage(ex));
        Assert.Contains($"Speech:Provider is {provider}", DeepMessage(ex));
    }

    /// <summary>⚠️ Matcha's own rule, and the reason <c>SherpaTtsOptions.FamilyInvalid</c> exists. Matcha
    /// is an acoustic model: it produces a spectrogram, and a separate vocoder — published in a different
    /// release from the voice — turns that into audio. A directory holding everything the voice archive
    /// shipped is still not something that can speak.</summary>
    [Fact]
    public void Matcha_without_a_vocoder_named_is_refused_before_it_reaches_the_model()
    {
        var ex = Assert.ThrowsAny<Exception>(() => FingerprintFor("Matcha", extra: new()
        {
            // A space rather than "": an in-memory configuration value that is empty is a value a binder
            // may or may not hand on, and this case is about the RULE, not about that ambiguity. The
            // empty string is pinned directly on the settings object in SherpaTtsEngineTests.
            ["Speech:Matcha:VocoderFile"] = " ",
        }));

        Assert.Contains("Speech:Matcha:VocoderFile", DeepMessage(ex));
    }

    /// <summary>And a vocoder that is NAMED but not on disk is refused too, naming the file. Separate from
    /// the case above because they fail in different places — one is a settings rule, the other is the
    /// disk check — and sherpa-onnx answers either one by killing the process.</summary>
    [Fact]
    public void Matcha_with_a_vocoder_that_is_not_there_is_refused_naming_the_file()
    {
        var ex = Assert.ThrowsAny<Exception>(() => FingerprintFor("Matcha", extra: new()
        {
            ["Speech:Matcha:VocoderFile"] = "hifigan_v2.onnx",
        }));

        Assert.Contains("hifigan_v2.onnx", DeepMessage(ex));
    }

    /// <summary>A complete, EMPTY model directory for one family. Empty because registration checks that
    /// the parts are on disk — all it can check without loading — and nothing here gets as far as reading
    /// one, which is itself the assertion that resolving the voice does not touch native code.</summary>
    private string AModelFor(string provider)
    {
        // Piper's directory carries a real archive's name, because Piper reads its weights' name off it —
        // the others' weights have one fixed name whatever the directory is called.
        var directory = provider == "Piper"
            ? Path.Combine(_dir, "models", provider, "vits-piper-en_US-ryan-high")
            : Path.Combine(_dir, "models", provider);
        Directory.CreateDirectory(directory);

        string[] parts = provider switch
        {
            "Kokoro" => ["model.int8.onnx", "voices.bin", "tokens.txt"],
            "Piper" => ["en_US-ryan-high.onnx", "tokens.txt"],
            "Matcha" => ["model-steps-3.onnx", "vocos-22khz-univ.onnx", "tokens.txt"],
            "Kitten" => ["model.fp16.onnx", "voices.bin", "tokens.txt"],
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Not a local family."),
        };

        foreach (var part in parts) File.WriteAllBytes(Path.Combine(directory, part), []);
        Directory.CreateDirectory(Path.Combine(directory, "espeak-ng-data"));
        return directory;
    }

    private string FingerprintFor(
        string provider,
        bool withModels = true,
        bool configureEveryFamily = false,
        Dictionary<string, string?>? extra = null)
    {
        var settings = extra ?? [];
        settings["Speech:Provider"] = provider;

        if (withModels)
        {
            // ⚠️ Looked up case-INSENSITIVELY, because one of the cases above deliberately passes
            // "kitten" to prove the app reads the setting that way. The model directory it configures
            // still has to be Kitten's; a helper that matched on spelling would fail those cases for a
            // reason that has nothing to do with what they are testing.
            List<(string Family, string Section)> families = configureEveryFamily
                ? [.. Families]
                : [Canonical(provider)];

            foreach (var (family, section) in families)
                settings[$"{section}:ModelDirectory"] = AModelFor(family);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IVoiceCredentials>(_ => new NoCredentials());
        services.AddScoped<ICurrentHousehold>(_ => new FakeCurrentHousehold());
        services.AddSpeech(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), _dir);

        using var root = services.BuildServiceProvider(validateScopes: true);
        using var scope = root.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITextToSpeech>().OutputFingerprint;
    }

    /// <summary>The families as a typed sequence rather than as theory rows.</summary>
    private static IEnumerable<(string Family, string Section)> Families =>
        EveryLocalFamily.Select(row => ((string)row[0], (string)row[1]));

    /// <summary>The canonically-spelled family a provider setting names, however it was typed.</summary>
    private static (string Family, string Section) Canonical(string provider) =>
        Families.Single(f => string.Equals(f.Family, provider, StringComparison.OrdinalIgnoreCase));

    // DI may wrap a registration exception in a factory error, so assert against the whole chain.
    private static string DeepMessage(Exception ex)
    {
        var message = ex.Message;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            message += " | " + inner.Message;
        return message;
    }

    private sealed class NoCredentials : IVoiceCredentials
    {
        public string ApiKey => "";
        public string AgentId => "";
    }
}
