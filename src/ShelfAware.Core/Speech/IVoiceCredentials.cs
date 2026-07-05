namespace ShelfAware.Core.Speech;

/// <summary>
/// The ElevenLabs credentials to use for the current request/circuit. Under BYOK these come from the
/// visitor's browser (scoped per circuit, so concurrent visitors never share a key); in local dev they
/// fall back to server config. Defined in Core so the Llm speech services can depend on it without
/// referencing the Web layer — the same interface-seam idea as IChatClient.
/// </summary>
public interface IVoiceCredentials
{
    /// <summary>ElevenLabs API key, or empty when the visitor hasn't set one.</summary>
    string ApiKey { get; }

    /// <summary>Optional cook-along conversational-agent id.</summary>
    string AgentId { get; }
}
