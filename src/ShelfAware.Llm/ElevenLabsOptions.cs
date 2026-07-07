namespace ShelfAware.Llm;

/// <summary>
/// ElevenLabs configuration for both speech directions (Scribe STT + TTS). Bound from the
/// "ElevenLabs" configuration section; the API key comes from user-secrets locally, App Service
/// settings on Azure — never committed (mirrors <see cref="LlmOptions"/>).
/// </summary>
public class ElevenLabsOptions
{
    public const string SectionName = "ElevenLabs";

    public string ApiKey { get; set; } = "";

    /// <summary>Scribe speech-to-text model. scribe_v1 is the stable default; scribe_v2 is also available.</summary>
    public string SpeechToTextModel { get; set; } = "scribe_v1";

    /// <summary>Text-to-speech model. Flash v2.5 is ElevenLabs' ~75 ms low-latency model, recommended for
    /// conversational / read-aloud voice loops (our push-to-talk + cook-along case).</summary>
    public string TextToSpeechModel { get; set; } = "eleven_flash_v2_5";

    /// <summary>Voice used for synthesis. Default is a stock ElevenLabs voice; override to taste.</summary>
    public string VoiceId { get; set; } = "JBFqnCBsd6RMkjVDRZzb";

    /// <summary>TTS output encoding (ElevenLabs output_format query value). mp3_44100_128 plays natively in browsers.</summary>
    public string OutputFormat { get; set; } = "mp3_44100_128";

    /// <summary>Conversational-AI agent id for the hands-free recipe cook-along (v2.1). Empty = the
    /// cook-along button is hidden. Not a secret (it's referenced client-side), but account-specific,
    /// so it's configured rather than hard-coded.</summary>
    public string AgentId { get; set; } = "";

    /// <summary>Daily cook-along signed-URL quota per household on a MANAGED deployment (each mint opens
    /// a realtime session on the host's ElevenLabs key), or null = unlimited (the self-host default).</summary>
    public int? DailySignedUrlLimit { get; set; }
}
