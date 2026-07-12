namespace ShelfAware.Llm;

/// <summary>Which LLM provider backs the AI services. Chosen from config in local dev, or per visitor
/// under BYOK (the deployed, source-available app ships with no keys of its own).</summary>
public enum AiProvider
{
    Anthropic,
    OpenAI,

    /// <summary>Any OpenAI-compatible endpoint at a custom base URL — a locally run model (Ollama, LM Studio,
    /// llama.cpp, vLLM) or a self-hosted gateway. The "run it fully local, zero token cost" path. On a hosted
    /// deploy a browser-supplied base URL is honored ONLY when the server sets <c>Llm:AllowCustomEndpoint</c>,
    /// so a visitor can't point the server at an arbitrary host (no SSRF).</summary>
    OpenAICompatible,
}
