using ShelfAware.Core.Speech;
using ShelfAware.Llm;

namespace ShelfAware.Web.Services;

/// <summary>
/// Composition of the voice I/O stack, in one place so the wiring is testable rather than asserted by
/// eye in Program.cs — in particular that <see cref="ITextToSpeech"/> resolves to the CACHE. Nothing
/// should be able to reach the provider directly and quietly re-buy audio we already own.
/// </summary>
public static class SpeechRegistration
{
    /// <summary>
    /// Registers ElevenLabs speech: Scribe = STT (ear), TTS = mouth, with TTS wrapped in a disk cache
    /// at <paramref name="cacheDirectory"/>. Speech is its own REST API rather than an IChatClient
    /// workload, so each rides a typed HttpClient. Typed clients are transient (the factory owns handler
    /// lifetime) — fine, the services are stateless.
    ///
    /// Requires a scoped <see cref="IVoiceCredentials"/> registered by the caller: the key is per-circuit
    /// (the visitor's own), so it is attached per request rather than baked into a default header.
    /// </summary>
    public static IServiceCollection AddSpeech(
        this IServiceCollection services, IConfiguration configuration, string cacheDirectory)
    {
        services.Configure<ElevenLabsOptions>(configuration.GetSection(ElevenLabsOptions.SectionName));
        services.AddHttpClient<ISpeechToText, ElevenLabsSpeechToText>(ConfigureElevenLabs);

        // The provider is registered concretely and the cache is what answers ITextToSpeech.
        services.AddHttpClient<ElevenLabsTextToSpeech>(ConfigureElevenLabs);
        services.AddTransient<ITextToSpeech>(sp => new CachingTextToSpeech(
            sp.GetRequiredService<ElevenLabsTextToSpeech>(),
            cacheDirectory,
            sp.GetRequiredService<ILogger<CachingTextToSpeech>>()));

        return services;
    }

    private static void ConfigureElevenLabs(IServiceProvider sp, HttpClient http)
    {
        // Base address only — the xi-api-key is attached PER REQUEST from the visitor's per-circuit
        // credentials (CircuitVoiceCredentials), never baked in as a default header.
        http.BaseAddress = new Uri("https://api.elevenlabs.io");
    }
}
