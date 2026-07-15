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

    /// <summary>Spell numbers, fractions and unit abbreviations out into words (via
    /// <see cref="ShelfAware.Core.Speech.SpeechText"/>) before synthesis. On by default because our
    /// model does NOT do it for us: ElevenLabs disables normalization on Flash v2.5 to protect its
    /// latency and gates apply_text_normalization behind Enterprise, and their docs note Flash
    /// mis-reads numbers that Multilingual v2 handles. Turn this off only if you switch
    /// <see cref="TextToSpeechModel"/> to a model that normalizes for you.</summary>
    public bool NormalizeText { get; set; } = true;

    /// <summary>Voice settings sent with every synthesis request.</summary>
    public ElevenLabsVoiceSettings VoiceSettings { get; set; } = new();

    /// <summary>Conversational-AI agent id for the hands-free recipe cook-along (v2.1). Empty = the
    /// cook-along button is hidden. Not a secret (it's referenced client-side), but account-specific,
    /// so it's configured rather than hard-coded.</summary>
    public string AgentId { get; set; } = "";

    /// <summary>Daily cook-along signed-URL quota per household on a MANAGED deployment (each mint opens
    /// a realtime session on the host's ElevenLabs key), or null = unlimited (the self-host default).</summary>
    public int? DailySignedUrlLimit { get; set; }
}

/// <summary>
/// ElevenLabs voice_settings. Every value is nullable and omitted from the request when null, so a
/// setting a given model rejects can be switched off in configuration without taking the rest down
/// with it. Defaults follow ElevenLabs' documented defaults except <see cref="Speed"/>.
/// </summary>
public class ElevenLabsVoiceSettings
{
    /// <summary>How consistent the delivery is between generations (ElevenLabs default 0.5). Lower is
    /// more expressive and more variable; higher is flatter and more predictable. A step-by-step
    /// reader wants consistency across segments more than it wants performance.</summary>
    public double? Stability { get; set; } = 0.5;

    /// <summary>How closely the model adheres to the original voice (ElevenLabs default 0.75).</summary>
    public double? SimilarityBoost { get; set; } = 0.75;

    /// <summary>Style exaggeration (ElevenLabs default 0). Left at zero — instructions read straight.</summary>
    public double? Style { get; set; } = 0;

    /// <summary>Boosts similarity to the original speaker (ElevenLabs default true).</summary>
    public bool? UseSpeakerBoost { get; set; } = true;

    /// <summary>Speaking rate; 1.0 is the ElevenLabs default. Defaulted under it because this voice reads
    /// cooking steps to someone whose hands are busy — they need to follow along, not keep up. Set by ear
    /// (0.95 still read a touch fast). Measured on one step: 1.0 = 12.0s, 0.95 = 12.8s, 0.90 = 13.9s,
    /// 0.85 = 14.4s — and 0.80 comes out the same length as 0.85, so 0.85 is the floor and lower values
    /// buy nothing. Set to null to send nothing and take the model's own default.</summary>
    public double? Speed { get; set; } = 0.90;
}
