namespace ShelfAware.Llm;

/// <summary>Which TTS provider synthesizes read-aloud audio. The STT ("ear") stays ElevenLabs Scribe —
/// moving speech RECOGNITION off ElevenLabs is a separate seam.</summary>
public enum SpeechProvider
{
    /// <summary>ElevenLabs cloud TTS (per-character cost, per-circuit key). The historical default, kept so
    /// no existing deployment changes on upgrade.</summary>
    ElevenLabs,

    /// <summary>A self-hosted, OpenAI-compatible TTS sidecar on the box (Kokoro-FastAPI). $0 per call — no
    /// key, no per-character fee, nothing to meter — which is why the managed demo box uses it.</summary>
    Local,
}

/// <summary>
/// Configuration for the <see cref="LocalTextToSpeech"/> sidecar (bound from the "Speech:Local" section).
/// The sidecar is trusted BOX infrastructure on the same host, so — unlike ElevenLabs — there is no
/// per-circuit / BYOK key: any credential here is a static server secret, like the managed LLM key.
/// </summary>
public class LocalSpeechOptions
{
    public const string SectionName = "Speech:Local";

    /// <summary>Origin of the local TTS sidecar. The synthesizer POSTs to <c>{BaseUrl}/v1/audio/speech</c>
    /// (the OpenAI-compatible endpoint Kokoro-FastAPI exposes). Must be an absolute http(s) URI — a
    /// loopback address on a box that runs the sidecar locally.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8880";

    /// <summary>Voice the sidecar exposes (Kokoro: e.g. <c>af_heart</c>, <c>af_bella</c>, <c>am_michael</c>).
    /// Part of the cache fingerprint, so changing it retires clips voiced the old way.</summary>
    public string Voice { get; set; } = "af_heart";

    /// <summary>Model name the sidecar expects. Kokoro-FastAPI answers to <c>kokoro</c>.</summary>
    public string Model { get; set; } = "kokoro";

    /// <summary>Audio container to request. <c>mp3</c> plays natively in browsers; wav/opus/flac also work
    /// if the sidecar supports them.</summary>
    public string Format { get; set; } = "mp3";

    /// <summary>Speaking rate; 1.0 is normal. Defaulted under 1.0 for the same reason as the ElevenLabs
    /// reader — someone cooking with busy hands needs to follow along, not keep up.</summary>
    public double Speed { get; set; } = 0.9;

    /// <summary>Optional bearer token, ONLY if the sidecar is secured (a shared instance on a LAN). Blank
    /// (the default) sends no Authorization header, which is right for a loopback sidecar that needs none.
    /// This is box config, never a visitor's key.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Spell numbers, fractions and unit abbreviations out into words (via
    /// <see cref="ShelfAware.Core.Speech.SpeechText"/>) before synthesis. On by default so pronunciation
    /// is consistent with the ElevenLabs path and the cache fingerprint's spelling rules mean the same
    /// thing whichever provider voiced a clip. Turn off only if the sidecar's own text handling is
    /// preferred.</summary>
    public bool NormalizeText { get; set; } = true;
}
