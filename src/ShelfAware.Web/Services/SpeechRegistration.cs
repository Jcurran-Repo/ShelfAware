using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;
using ShelfAware.Llm;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>
/// Composition of the voice I/O stack, in one place so the wiring is testable rather than asserted by
/// eye in Program.cs — in particular that <see cref="ITextToSpeech"/> resolves to the CACHE. Nothing
/// should be able to reach the provider directly and quietly re-buy audio we already own.
/// </summary>
public static class SpeechRegistration
{
    /// <summary>
    /// Registers speech: STT = ear, and TTS = mouth wrapped in a disk cache at
    /// <paramref name="cacheDirectory"/>. The cloud services are their own REST APIs rather than
    /// IChatClient workloads, so each rides a typed HttpClient; typed clients are transient (the factory
    /// owns handler lifetime) — fine, the services are stateless. A local voice rides nothing, because it
    /// runs here.
    ///
    /// <para>The TTS PROVIDER is chosen by <c>Speech:Provider</c> (default ElevenLabs, so no existing
    /// deployment changes on upgrade): <see cref="SpeechProvider.Kokoro"/>, <see cref="SpeechProvider.Piper"/>,
    /// <see cref="SpeechProvider.Kitten"/> and <see cref="SpeechProvider.Matcha"/> all run a model IN THIS
    /// PROCESS for $0 synthesis, differing in warmth against speed (docs/voice-bakeoff.md);
    /// <see cref="SpeechProvider.ElevenLabs"/> keeps the cloud voice. The EAR is chosen
    /// separately by <c>Speech:Ear</c> (<see cref="EarProvider"/>), so a box can move one before the
    /// other. Whichever TTS provider is chosen, it's the CACHE that answers <see cref="ITextToSpeech"/>; the provider
    /// is only ever reached through it.</para>
    ///
    /// Requires a scoped <see cref="IVoiceCredentials"/> registered by the caller: the ElevenLabs key is
    /// per-circuit (the visitor's own), so it is attached per request rather than baked into a default
    /// header. A local voice needs no such credential — there is nobody to pay (see
    /// <see cref="SherpaTtsOptions"/>).
    /// </summary>
    /// <param name="cacheDirectory">Where synthesized audio lives, or null to synthesize every time. Null
    /// is what <c>Speech:CacheMegabytes = 0</c> means: someone asking for no cache should GET no cache,
    /// not an empty one that refills all session and gets wiped at the next boot — that would re-buy every
    /// recipe after each restart, use the disk anyway, and say nothing.</param>
    public static IServiceCollection AddSpeech(
        this IServiceCollection services, IConfiguration configuration, string? cacheDirectory)
    {
        services.Configure<ElevenLabsOptions>(configuration.GetSection(ElevenLabsOptions.SectionName));
        // ⚠️ No Configure<> for the two MOUTH families, deliberately. The chosen one is bound once below
        // (LocalVoiceOf) and handed to the engine and the voice over it as an object, because the family
        // is a registration-time decision — an IOptions<T> nobody resolves is a setting a later
        // services.Configure<PiperSpeechOptions>(o => …) would appear to change while changing nothing.
        // The EAR still goes through IOptions because SherpaMoonshineEngine resolves it that way.
        services.Configure<MoonshineSpeechOptions>(configuration.GetSection(MoonshineSpeechOptions.SectionName));
        RefuseRetiredSidecarSettings(configuration);

        // The EAR, chosen by Speech:Ear independently of the mouth (see EarProvider): a box can voice
        // recipes for free while still renting recognition, or the reverse, and one setting covering both
        // would mean a deployment could not be half-way through the move. Default ElevenLabs, so no
        // existing deployment changes on upgrade.
        if (EarOf(configuration) == EarProvider.Moonshine)
        {
            // No HttpClient: there is nothing to talk to. The ENGINE is a singleton because the model is
            // the expensive thing; the ISpeechToText over it stays transient like its siblings.
            RequireAnEarModelOnDisk(configuration);
            services.AddSingleton<IMoonshineEngine, SherpaMoonshineEngine>();
            services.AddTransient<ISpeechToText, MoonshineSpeechToText>();
        }
        else
        {
            services.AddHttpClient<ISpeechToText, ElevenLabsSpeechToText>(ConfigureElevenLabs);
        }

        var provider = configuration.GetValue<SpeechProvider?>("Speech:Provider") ?? SpeechProvider.ElevenLabs;

        // Register the chosen provider by its own concrete type and expose a resolver for it — so the
        // cache, or the direct registration below, can wrap it as the inner ITextToSpeech without either
        // caring which provider it is, or whether it reaches a network at all.
        Func<IServiceProvider, ITextToSpeech> resolveProvider;
        if (LocalVoiceOf(configuration, provider) is { } localVoice)
        {
            // No HttpClient: there is nothing to talk to. The ENGINE is a singleton because the model is
            // the expensive thing (a second or more to load, a few hundred MB resident); the ITextToSpeech
            // over it stays transient like its siblings.
            //
            // ⚠️ The chosen family's settings are resolved ONCE, here, and handed to both the engine and
            // the voice over it. Letting either ask for its own options would mean two readings of "which
            // family is this box running", which is how a box ends up loading Piper's model and
            // fingerprinting its clips as Kokoro's.
            RequireAModelOnDisk(localVoice, provider);
            services.AddSingleton<ITtsEngine>(sp => new SherpaTtsEngine(
                localVoice, sp.GetRequiredService<ILogger<SherpaTtsEngine>>()));
            services.AddTransient(sp => new SherpaTextToSpeech(
                sp.GetRequiredService<ITtsEngine>(),
                localVoice,
                sp.GetRequiredService<ILogger<SherpaTextToSpeech>>()));
            resolveProvider = sp => sp.GetRequiredService<SherpaTextToSpeech>();
        }
        else
        {
            services.AddHttpClient<ElevenLabsTextToSpeech>(ConfigureElevenLabs);
            resolveProvider = sp => sp.GetRequiredService<ElevenLabsTextToSpeech>();
        }

        if (cacheDirectory is null)
        {
            services.AddTransient(resolveProvider);
            return services;
        }

        // The cache is what answers ITextToSpeech; it reads ICurrentHousehold (scoped) per call so clips
        // are filed per household, never shared.
        services.AddTransient(sp => new CachingTextToSpeech(
            resolveProvider(sp),
            cacheDirectory,
            sp.GetRequiredService<ICurrentHousehold>(),
            sp.GetRequiredService<ILogger<CachingTextToSpeech>>()));
        services.AddTransient<ITextToSpeech>(sp => sp.GetRequiredService<CachingTextToSpeech>());
        // Registered ONLY when there's a cache, so a null ISpeechCache means exactly "no cache" rather
        // than an empty one that finds nothing and deletes nothing while claiming otherwise.
        services.AddTransient<ISpeechCache>(sp => sp.GetRequiredService<CachingTextToSpeech>());

        return services;
    }

    /// <summary>Which ear this deployment runs, from the ONE reading of the setting — asked by
    /// registration and by <see cref="CircuitVoiceCredentials"/>, which needs it to answer "can this box
    /// hear" for every microphone affordance. Two readings of the same key is how a box ends up offering
    /// a mic because one of them said Moonshine and refusing to use it because the other said
    /// ElevenLabs.</summary>
    public static EarProvider EarOf(IConfiguration configuration) =>
        configuration.GetValue<EarProvider?>("Speech:Ear") ?? EarProvider.ElevenLabs;

    private static void ConfigureElevenLabs(IServiceProvider sp, HttpClient http)
    {
        // Base address only — the xi-api-key is attached PER REQUEST from the visitor's per-circuit
        // credentials (CircuitVoiceCredentials), never baked in as a default header.
        http.BaseAddress = new Uri("https://api.elevenlabs.io");
    }

    /// <summary>The settings for whichever local family <paramref name="provider"/> names, or null when
    /// it names the cloud voice. ⚠️ ONE reading of "which family is this box running", handed to the
    /// engine and the voice over it, so the model that loads and the fingerprint its clips are filed
    /// under can never disagree.</summary>
    private static SherpaTtsOptions? LocalVoiceOf(IConfiguration configuration, SpeechProvider provider) =>
        provider switch
        {
            SpeechProvider.Kokoro => Bind<KokoroSpeechOptions>(configuration, KokoroSpeechOptions.SectionName),
            SpeechProvider.Piper => Bind<PiperSpeechOptions>(configuration, PiperSpeechOptions.SectionName),
            SpeechProvider.Matcha => Bind<MatchaSpeechOptions>(configuration, MatchaSpeechOptions.SectionName),
            SpeechProvider.Kitten => Bind<KittenSpeechOptions>(configuration, KittenSpeechOptions.SectionName),
            _ => null,
        };

    private static T Bind<T>(IConfiguration configuration, string section) where T : new() =>
        configuration.GetSection(section).Get<T>() ?? new T();

    /// <summary>
    /// ⚠️ Refuse to boot with a model directory that isn't there. This is not defensive tidiness: the
    /// native library answers a missing model file by printing one line to stderr and killing the process
    /// with a SIGSEGV — no managed exception, nothing to catch, nothing of ours in the log. Without this
    /// check the app would start clean and then die whole on the first read-aloud, taking every circuit
    /// with it, and the only clue would be a stderr line nobody was watching.
    /// <para>It asks <see cref="SherpaTtsOptions.Model"/> — the same definition the engine loads from —
    /// because a validation that checked a different set of paths than the load uses would pass and then
    /// crash. The settings it can judge without the disk go through <see cref="SherpaTtsOptions.Invalid"/>,
    /// which the engine also asks, for the same reason.</para>
    /// </summary>
    private static void RequireAModelOnDisk(SherpaTtsOptions options, SpeechProvider provider)
    {
        // Everything judgeable from the settings alone, asked of the one definition so registration and
        // the engine cannot come to different conclusions about the same configuration.
        if (options.Invalid() is { } wrong)
            throw new InvalidOperationException($"Speech:Provider is {provider}, but {wrong}");

        if (options.Model().Missing() is { Count: > 0 } missing)
            throw new InvalidOperationException(
                $"{options.Section}:ModelDirectory ('{options.ModelDirectory}') is not a complete "
                + $"{provider} model: {string.Join(", ", missing)} not found. See docs/ for the archive to "
                + "unpack there. (Starting without them would not fail here — it would kill the process on "
                + "the first read-aloud.)");
    }

    /// <summary>
    /// The ear's <see cref="RequireAModelOnDisk"/>, and it exists for exactly the same reason: sherpa's
    /// native library answers a missing model file with a line on stderr and a SIGSEGV, so a box told to
    /// listen with a model that isn't there would boot clean and then die whole on the first spoken word.
    /// Asks <see cref="MoonshineModelFiles"/> — the same definition the engine loads from — because a
    /// validation checking different paths than the load uses would pass and then crash.
    /// </summary>
    private static void RequireAnEarModelOnDisk(IConfiguration configuration)
    {
        var options = configuration.GetSection(MoonshineSpeechOptions.SectionName).Get<MoonshineSpeechOptions>()
                      ?? new MoonshineSpeechOptions();

        if (options.Invalid() is { } wrong)
            throw new InvalidOperationException($"Speech:Ear is Moonshine, but {wrong}");

        if (MoonshineModelFiles.In(options.ModelDirectory).Missing() is { Count: > 0 } missing)
            throw new InvalidOperationException(
                $"Speech:Moonshine:ModelDirectory ('{options.ModelDirectory}') is not a complete Moonshine "
                + $"model: {string.Join(", ", missing)} not found. See docs/deploy-moonshine.md for the "
                + "archive to unpack there. (Starting without them would not fail here — it would kill the "
                + "process on the first spoken word.)");
    }

    /// <summary>
    /// ⚠️ A box that upgrades past the sidecar gets told, rather than quietly ignored. The read-aloud voice
    /// used to be an HTTP sidecar configured under <c>Speech:Local</c>; it is now in-process under
    /// <c>Speech:Kokoro</c>. Configuration binding drops keys nothing binds, so an env file still carrying
    /// <c>Speech__Provider=Local</c> and a tuned <c>Speech__Local__Speed</c> would boot happily on
    /// ElevenLabs at someone else's expense, or on Kokoro at a speed nobody chose — and nothing would say
    /// so. The whole point of retiring a setting is that its absence is loud.
    /// </summary>
    private static void RefuseRetiredSidecarSettings(IConfiguration configuration)
    {
        var retired = configuration.GetSection("Speech:Local").GetChildren().Select(c => c.Path).ToList();

        // The enum's old member name, which no longer parses to anything and would otherwise fail with a
        // message about a bad enum value rather than about what replaced it.
        if (string.Equals(configuration["Speech:Provider"], "Local", StringComparison.OrdinalIgnoreCase))
            retired.Add("Speech:Provider=Local");

        if (retired.Count == 0) return;

        throw new InvalidOperationException(
            "The Kokoro read-aloud voice runs in this process now, not in an HTTP sidecar, so these "
            + $"settings no longer do anything: {string.Join(", ", retired)}. Use Speech:Provider=Kokoro "
            + "and the Speech:Kokoro section (ModelDirectory, SpeakerId, Speed, NumThreads) instead, and "
            + "retire the sidecar service. See docs/deploy-kokoro.md.");
    }

}
