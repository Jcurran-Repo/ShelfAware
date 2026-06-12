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
}
