namespace ShelfAware.Llm;

/// <summary>
/// Which model an AI call runs on — the ONE question every AI service asks when it builds its
/// <c>ChatOptions.ModelId</c>, read at CALL time rather than captured at construction.
///
/// <para>⚠️ Why this exists. The services used to read the server's <see cref="LlmOptions"/> directly, so a
/// model a BYOK visitor picked in Settings only bound the client: every call still named the server's model,
/// and a call's own <c>ModelId</c> wins over the client's. A visitor on Sonnet ran on Haiku; a visitor on the
/// OpenAI provider sent a Claude model id to OpenAI. Under the web app this is the circuit's
/// <c>CircuitAiSettings</c> (server config, overlaid by the visitor's own choice on a BYOK box); anywhere
/// with no circuit — the eval harness, the tests — it is <see cref="LlmOptions"/> itself.</para>
///
/// <para>Read it per call, never into a field: under BYOK the visitor's settings reach the circuit after
/// the services have been constructed.</para>
/// </summary>
public interface IAiModelChoice
{
    /// <summary>The model for reading things: receipts, shelf photos, recipe photos, and the short
    /// classification advisors.</summary>
    string ExtractionModel { get; }

    /// <summary>The model for conversation and generation: chat, recipe suggestions, meal plans, and
    /// structuring pasted recipe text.</summary>
    string ChatModel { get; }
}
