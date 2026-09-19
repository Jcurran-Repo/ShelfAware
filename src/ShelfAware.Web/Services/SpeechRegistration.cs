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
    /// Registers speech: Scribe = STT (ear), and TTS = mouth wrapped in a disk cache at
    /// <paramref name="cacheDirectory"/>. The cloud services are their own REST APIs rather than
    /// IChatClient workloads, so each rides a typed HttpClient; typed clients are transient (the factory
    /// owns handler lifetime) — fine, the services are stateless. Kokoro rides nothing, because it runs
    /// here.
    ///
    /// <para>The TTS PROVIDER is chosen by <c>Speech:Provider</c> (default ElevenLabs, so no existing
    /// deployment changes on upgrade): <see cref="SpeechProvider.Kokoro"/> runs Kokoro-82M IN THIS PROCESS
    /// for $0 synthesis; <see cref="SpeechProvider.ElevenLabs"/> keeps the cloud voice. The STT ear stays
    /// ElevenLabs Scribe either way — moving speech RECOGNITION off ElevenLabs is a separate seam.
    /// Whichever provider is chosen, it's the CACHE that answers <see cref="ITextToSpeech"/>; the provider
    /// is only ever reached through it.</para>
    ///
    /// Requires a scoped <see cref="IVoiceCredentials"/> registered by the caller: the ElevenLabs key is
    /// per-circuit (the visitor's own), so it is attached per request rather than baked into a default
    /// header. Kokoro needs no such credential — there is nobody to pay (see
    /// <see cref="KokoroSpeechOptions"/>).
    /// </summary>
    /// <param name="cacheDirectory">Where synthesized audio lives, or null to synthesize every time. Null
    /// is what <c>Speech:CacheMegabytes = 0</c> means: someone asking for no cache should GET no cache,
    /// not an empty one that refills all session and gets wiped at the next boot — that would re-buy every
    /// recipe after each restart, use the disk anyway, and say nothing.</param>
    public static IServiceCollection AddSpeech(
        this IServiceCollection services, IConfiguration configuration, string? cacheDirectory)
    {
        services.Configure<ElevenLabsOptions>(configuration.GetSection(ElevenLabsOptions.SectionName));
        services.Configure<KokoroSpeechOptions>(configuration.GetSection(KokoroSpeechOptions.SectionName));
        RefuseRetiredSidecarSettings(configuration);

        // The ear is always ElevenLabs Scribe; only the mouth's provider is selectable.
        services.AddHttpClient<ISpeechToText, ElevenLabsSpeechToText>(ConfigureElevenLabs);

        var provider = configuration.GetValue<SpeechProvider?>("Speech:Provider") ?? SpeechProvider.ElevenLabs;

        // Register the chosen provider by its own concrete type and expose a resolver for it — so the
        // cache, or the direct registration below, can wrap it as the inner ITextToSpeech without either
        // caring which provider it is, or whether it reaches a network at all.
        Func<IServiceProvider, ITextToSpeech> resolveProvider;
        if (provider == SpeechProvider.Kokoro)
        {
            // No HttpClient: there is nothing to talk to. The ENGINE is a singleton because the model is
            // the expensive thing (~1.6 s to load, a few hundred MB resident); the ITextToSpeech over it
            // stays transient like its siblings.
            RequireAModelOnDisk(configuration);
            services.AddSingleton<IKokoroEngine, SherpaKokoroEngine>();
            services.AddTransient<KokoroTextToSpeech>();
            resolveProvider = sp => sp.GetRequiredService<KokoroTextToSpeech>();
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

    private static void ConfigureElevenLabs(IServiceProvider sp, HttpClient http)
    {
        // Base address only — the xi-api-key is attached PER REQUEST from the visitor's per-circuit
        // credentials (CircuitVoiceCredentials), never baked in as a default header.
        http.BaseAddress = new Uri("https://api.elevenlabs.io");
    }

    /// <summary>
    /// ⚠️ Refuse to boot with a model directory that isn't there. This is not defensive tidiness: the
    /// native library answers a missing model file by printing one line to stderr and killing the process
    /// with a SIGSEGV — no managed exception, nothing to catch, nothing of ours in the log. Without this
    /// check the app would start clean and then die whole on the first read-aloud, taking every circuit
    /// with it, and the only clue would be a stderr line nobody was watching.
    /// <para>It asks <see cref="KokoroModelFiles"/> — the same definition the engine loads from — because
    /// a validation that checked a different set of paths than the load uses would pass and then crash.
    /// The settings it can judge without the disk go through <see cref="KokoroSpeechOptions.Invalid"/>,
    /// which the engine also asks, for the same reason.</para>
    /// </summary>
    private static void RequireAModelOnDisk(IConfiguration configuration)
    {
        var options = configuration.GetSection(KokoroSpeechOptions.SectionName).Get<KokoroSpeechOptions>()
                      ?? new KokoroSpeechOptions();

        // Everything judgeable from the settings alone, asked of the one definition so registration and
        // the engine cannot come to different conclusions about the same configuration.
        if (options.Invalid() is { } wrong)
            throw new InvalidOperationException($"Speech:Provider is Kokoro, but {wrong}");

        if (KokoroModelFiles.In(options.ModelDirectory, options.ModelFile).Missing() is { Count: > 0 } missing)
            throw new InvalidOperationException(
                $"Speech:Kokoro:ModelDirectory ('{options.ModelDirectory}') is not a complete Kokoro model: "
                + $"{string.Join(", ", missing)} not found. See docs/deploy-kokoro.md for the archive to "
                + "unpack there. (Starting without them would not fail here — it would kill the process on "
                + "the first read-aloud.)");
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
