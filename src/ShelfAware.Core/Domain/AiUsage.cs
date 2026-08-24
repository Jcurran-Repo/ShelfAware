namespace ShelfAware.Core.Domain;

/// <summary>
/// One household's AI consumption for one day. Recorded in EVERY key mode so the user can see what
/// they've spent (the Settings usage panel); the daily quotas read it too, but those are ENFORCED
/// only on a managed-key deployment — BYOK visitors ride their own keys, recorded but never
/// limited. One row per (household, day) — the data a daily quota needs now, and the data any
/// future billing would need later.
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

    /// <summary>The day's LLM COST in micros (millionths of a dollar), accumulated per call from the
    /// pricing catalog at the rate in force when it was recorded (docs/subscription-plan.md §4 — stamped,
    /// not re-derived, so a later rate change never rewrites history). Recorded in EVERY key mode, like
    /// the rest of this row: on a managed call it's the operator's "what did this household cost me", and
    /// on a BYOK call it's the household's own key spend — either way theirs to see. Display-only; the
    /// credit LEDGER is the money authority (and it, unlike this, excludes BYOK).</summary>
    public long CostMicros { get; set; }

    /// <summary>Cook-along signed-URL mints (each opens a realtime ElevenLabs session on the host's key).</summary>
    public int VoiceSessionMints { get; set; }
}
