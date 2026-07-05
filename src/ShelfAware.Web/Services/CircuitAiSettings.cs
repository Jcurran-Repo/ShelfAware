using Microsoft.Extensions.Options;
using ShelfAware.Llm;

namespace ShelfAware.Web.Services;

/// <summary>
/// Per-circuit AI configuration. Defaults to the server config (<see cref="LlmOptions"/>) so local dev and
/// a self-hosted owner keep working with a user-secrets key; a visitor's browser-held settings override it
/// for their circuit (BYOK) via <see cref="Apply"/>. Scoped = one instance per Blazor circuit, so two
/// concurrent visitors never share a key.
/// </summary>
public sealed class CircuitAiSettings
{
    private readonly AiProvider _fallbackProvider;
    private readonly string _fallbackKey;
    private readonly string _fallbackExtractionModel;
    private readonly string _fallbackChatModel;

    public CircuitAiSettings(IOptions<LlmOptions> fallback)
    {
        var o = fallback.Value;
        _fallbackProvider = Enum.TryParse<AiProvider>(o.Provider, ignoreCase: true, out var p) ? p : AiProvider.Anthropic;
        _fallbackKey = o.ApiKey;
        _fallbackExtractionModel = o.ExtractionModel;
        _fallbackChatModel = o.ChatModel;
        Reset();
    }

    public AiProvider Provider { get; private set; }
    public string ApiKey { get; private set; } = "";
    public string ExtractionModel { get; private set; } = "";
    public string ChatModel { get; private set; } = "";

    /// <summary>True once the visitor's browser settings have been applied (vs the dev/config fallback).</summary>
    public bool FromBrowser { get; private set; }

    /// <summary>Whether an AI call can be attempted (a key is present for the chosen provider).</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Overlay the visitor's own settings (from their browser). Blank models keep the defaults.</summary>
    public void Apply(AiProvider provider, string apiKey, string? extractionModel, string? chatModel)
    {
        Provider = provider;
        ApiKey = apiKey ?? "";
        if (!string.IsNullOrWhiteSpace(extractionModel)) ExtractionModel = extractionModel;
        if (!string.IsNullOrWhiteSpace(chatModel)) ChatModel = chatModel;
        FromBrowser = true;
    }

    /// <summary>Revert to the server-config fallback — used when the visitor forgets their key. On a public
    /// deploy that's an empty key (AI off until they re-enter one); in local dev it's the owner key again.</summary>
    public void Reset()
    {
        Provider = _fallbackProvider;
        ApiKey = _fallbackKey;
        ExtractionModel = _fallbackExtractionModel;
        ChatModel = _fallbackChatModel;
        FromBrowser = false;
    }
}
