namespace ShelfAware.Llm;

/// <summary>Which LLM provider backs the AI services. Chosen from config in local dev, or per visitor
/// under BYOK (the deployed, open-source app ships with no keys of its own).</summary>
public enum AiProvider
{
    Anthropic,
    OpenAI,
}
