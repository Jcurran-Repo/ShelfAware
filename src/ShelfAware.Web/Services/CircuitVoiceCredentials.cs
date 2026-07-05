using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;
using ShelfAware.Llm;

namespace ShelfAware.Web.Services;

/// <summary>
/// Per-circuit ElevenLabs credentials. Defaults to the server config (<see cref="ElevenLabsOptions"/>) for
/// local dev / self-host; the visitor's browser overrides it per circuit (BYOK). Scoped so two concurrent
/// visitors never share a voice key. Parallels <see cref="CircuitAiSettings"/>. The key is only ever used
/// server-side to call ElevenLabs — never persisted, never logged.
/// </summary>
public sealed class CircuitVoiceCredentials : IVoiceCredentials
{
    private readonly string _fallbackKey;
    private readonly string _fallbackAgentId;

    public CircuitVoiceCredentials(IOptions<ElevenLabsOptions> fallback)
    {
        _fallbackKey = fallback.Value.ApiKey;
        _fallbackAgentId = fallback.Value.AgentId;
        Reset();
    }

    public string ApiKey { get; private set; } = "";
    public string AgentId { get; private set; } = "";

    /// <summary>True once the visitor's browser voice creds have been applied (vs the dev/config fallback).</summary>
    public bool FromBrowser { get; private set; }

    public void Apply(string? apiKey, string? agentId)
    {
        ApiKey = apiKey ?? "";
        AgentId = agentId ?? "";
        FromBrowser = true;
    }

    /// <summary>Revert to the server-config fallback (used on "forget my keys").</summary>
    public void Reset()
    {
        ApiKey = _fallbackKey;
        AgentId = _fallbackAgentId;
        FromBrowser = false;
    }
}
