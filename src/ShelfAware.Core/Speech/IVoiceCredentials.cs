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

    /// <summary>True when the host's keys are authoritative on this deployment and a browser-supplied
    /// one is ignored. Defaulted to false — the BYOK/self-host shape — so a stub or a test fake states
    /// only what it cares about; <c>CircuitVoiceCredentials</c> in the Web layer is the implementation
    /// that knows.</summary>
    bool Managed => false;

    /// <summary>Whether this deployment can hear, from the ONE definition every site asks — the mic
    /// affordances, the hands-free reader, and the transcriber's own failure copy. A default member
    /// rather than a property each implementer answers for itself: that is the whole point of it.</summary>
    EarAvailability Ear => VoiceEar.Availability(Managed, ApiKey);
}
