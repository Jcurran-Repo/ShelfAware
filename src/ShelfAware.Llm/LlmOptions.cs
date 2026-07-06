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
}
