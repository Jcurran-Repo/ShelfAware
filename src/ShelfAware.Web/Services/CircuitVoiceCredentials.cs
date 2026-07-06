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
    private readonly bool _managed;

    public CircuitVoiceCredentials(IOptions<ElevenLabsOptions> fallback, IOptions<LlmOptions> deployment)
    {
        _fallbackKey = fallback.Value.ApiKey;
        _fallbackAgentId = fallback.Value.AgentId;
        _managed = deployment.Value.IsManaged; // managed = the host's voice key too; ignore browser creds
        Reset();
    }

    public string ApiKey { get; private set; } = "";
    public string AgentId { get; private set; } = "";

    /// <summary>True once the visitor's browser voice creds have been applied (vs the dev/config fallback).</summary>
    public bool FromBrowser { get; private set; }

    public void Apply(string? apiKey, string? agentId)
    {
        if (_managed) return; // host's key is authoritative on a managed deployment
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
