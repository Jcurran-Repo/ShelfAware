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
    /// <paramref name="cacheDirectory"/>. Speech is its own REST API rather than an IChatClient workload,
    /// so each rides a typed HttpClient. Typed clients are transient (the factory owns handler lifetime) —
    /// fine, the services are stateless.
    ///
    /// <para>The TTS PROVIDER is chosen by <c>Speech:Provider</c> (default ElevenLabs, so no existing
    /// deployment changes on upgrade): <see cref="SpeechProvider.Local"/> points at a self-hosted,
    /// OpenAI-compatible sidecar (Kokoro) for $0 synthesis; <see cref="SpeechProvider.ElevenLabs"/> keeps
    /// the cloud voice. The STT ear stays ElevenLabs Scribe either way — moving speech RECOGNITION off
    /// ElevenLabs is a separate seam. Whichever provider is chosen, it's the CACHE that answers
    /// <see cref="ITextToSpeech"/>; the provider is only ever reached through it.</para>
    ///
    /// Requires a scoped <see cref="IVoiceCredentials"/> registered by the caller: the ElevenLabs key is
    /// per-circuit (the visitor's own), so it is attached per request rather than baked into a default
    /// header. The local sidecar needs no such credential (see <see cref="LocalSpeechOptions"/>).
    /// </summary>
    /// <param name="cacheDirectory">Where synthesized audio lives, or null to synthesize every time. Null
    /// is what <c>Speech:CacheMegabytes = 0</c> means: someone asking for no cache should GET no cache,
    /// not an empty one that refills all session and gets wiped at the next boot — that would re-buy every
    /// recipe after each restart, use the disk anyway, and say nothing.</param>
    public static IServiceCollection AddSpeech(
        this IServiceCollection services, IConfiguration configuration, string? cacheDirectory)
    {
        services.Configure<ElevenLabsOptions>(configuration.GetSection(ElevenLabsOptions.SectionName));
        services.Configure<LocalSpeechOptions>(configuration.GetSection(LocalSpeechOptions.SectionName));

        // The ear is always ElevenLabs Scribe; only the mouth's provider is selectable.
        services.AddHttpClient<ISpeechToText, ElevenLabsSpeechToText>(ConfigureElevenLabs);

        var provider = configuration.GetValue<SpeechProvider?>("Speech:Provider") ?? SpeechProvider.ElevenLabs;

        // Register the chosen provider by its own concrete type (with its typed HttpClient) and expose a
        // resolver for it — so the cache, or the direct registration below, can wrap it as the inner
        // ITextToSpeech without either caring which provider it is.
        Func<IServiceProvider, ITextToSpeech> resolveProvider;
        if (provider == SpeechProvider.Local)
        {
            services.AddHttpClient<LocalTextToSpeech>(ConfigureLocal);
            resolveProvider = sp => sp.GetRequiredService<LocalTextToSpeech>();
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

    private static void ConfigureLocal(IServiceProvider sp, HttpClient http)
    {
        var options = sp.GetRequiredService<IOptions<LocalSpeechOptions>>().Value;
        // A bad value would otherwise surface as an opaque failure on the first read-aloud; fail at
        // startup with a message that names the setting. Require a bare http(s) ORIGIN, no path: the
        // request URI is a leading-slash absolute path, so a BaseUrl carrying a subpath would be
        // silently dropped, and a non-http scheme (file://, ftp://) would fail later with a worse message.
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || baseUri.AbsolutePath.Trim('/').Length != 0)
            throw new InvalidOperationException(
                $"Speech:Local:BaseUrl must be a bare http(s) origin with no path " +
                $"(e.g. http://127.0.0.1:8880); got '{options.BaseUrl}'.");
        http.BaseAddress = baseUri;
    }
}
