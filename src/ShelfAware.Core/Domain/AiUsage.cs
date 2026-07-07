namespace ShelfAware.Core.Domain;

/// <summary>
/// One household's AI consumption for one day, on a MANAGED-key deployment (the host's wallet).
/// BYOK visitors ride their own keys and are never metered. One row per (household, day) — the
/// data a daily quota needs now, and the data any future billing would need later.
/// </summary>
public class AiUsage : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public DateOnly Day { get; set; }

    /// <summary>LLM calls made (chat turns, extractions, advisors — every IChatClient round-trip).</summary>
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Cook-along signed-URL mints (each opens a realtime ElevenLabs session on the host's key).</summary>
    public int VoiceSessionMints { get; set; }
}
