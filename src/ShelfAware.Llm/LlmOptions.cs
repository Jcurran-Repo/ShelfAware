namespace ShelfAware.Llm;

public class LlmOptions
{
    public const string SectionName = "Llm";

    public string Provider { get; set; } = "Anthropic";
    /// <summary>Pinned versioned model ID — never aliases (DESIGN.md §2).</summary>
    public string ExtractionModel { get; set; } = "claude-haiku-4-5-20251001";
    public string ChatModel { get; set; } = "claude-haiku-4-5-20251001";
    public int MaxImageEdgePx { get; set; } = 1568;
    public string ApiKey { get; set; } = "";

    /// <summary>Base URL for an OpenAI-compatible provider (e.g. <c>http://localhost:11434/v1</c> for Ollama).
    /// Ignored for Anthropic and OpenAI-proper.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Whether a custom OpenAI-compatible base URL may be used — including one a visitor sets in
    /// their browser. Default false: on the public demo the server never issues requests to a user-supplied
    /// URL. A self-hoster sets this true to point ShelfAware at their own local model.</summary>
    public bool AllowCustomEndpoint { get; set; }

    /// <summary>Daily LLM-call quota per household on a MANAGED deployment (the host's wallet), or null =
    /// unlimited (the self-host default). BYOK circuits are never metered — their key, their wallet.</summary>
    public int? DailyCallLimit { get; set; }

    /// <summary>Daily token quota (input + output combined) per household on a managed deployment, or
    /// null = unlimited. Whichever of the two limits trips first wins.</summary>
    public long? DailyTokenLimit { get; set; }

    /// <summary>Deployment key policy: "Managed" / "Byok", or "Auto" (default when unset). Managed = the
    /// host provides ALL keys (LLM + voice) and visitors use them without editing (a tailnet / Azure
    /// deploy); BYOK = visitors bring their own in the browser (the source-available self-host / public demo).
    /// Auto infers it — managed when a server key is configured, BYOK otherwise — so just dropping keys in
    /// secrets/appsettings flips a deployment to managed.</summary>
    public string? KeyMode { get; set; }

    /// <summary>True when this is a managed-key deployment: server-provided keys are authoritative and the
    /// Settings key panel is hidden. See <see cref="KeyMode"/>.</summary>
    public bool IsManaged => (KeyMode ?? "").Trim().ToLowerInvariant() switch
    {
        "managed" => true,
        "byok" => false,
        _ => !string.IsNullOrWhiteSpace(ApiKey), // Auto
    };
}
