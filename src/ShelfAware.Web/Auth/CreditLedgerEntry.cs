namespace ShelfAware.Web.Auth;

/// <summary>What kind of ledger movement this is. Phase 2 uses only <see cref="Grant"/> (the welcome
/// grant) and <see cref="Consumption"/> (an AI call drawing the balance down). Purchase / Expiry /
/// Refund join when payments land (phase 3) — the enum is extensible so a new kind is additive.</summary>
public enum CreditEntryKind
{
    Grant = 0,
    Consumption = 1,
}

/// <summary>
/// One movement in a household's credit ledger — the append-only money record (docs/subscription-plan.md
/// §4: "the auth-side LEDGER is THE money record; the pantry AiUsage row is display-only"). Balance is the
/// SUM of <see cref="AmountMicros"/> for a household, so nothing mutates a running total in place (the
/// read-modify-write races the invite-code work already taught).
///
/// Lives in auth.db beside accounts and the subscription (this is money/credential-adjacent, and it must
/// survive a pantry "delete my data" — destroying purchased credit is destroying money). auth.db has no
/// tenancy query filter, so every read/write hand-scopes its WHERE to the household (the ApiTokenService
/// pattern).
/// </summary>
public sealed class CreditLedgerEntry
{
    public int Id { get; set; }

    /// <summary>The household this movement belongs to. A plain indexed value (auth.db has no query
    /// filter), the way ApiToken carries it.</summary>
    public string HouseholdId { get; set; } = "";

    public CreditEntryKind Kind { get; set; }

    /// <summary>Signed RETAIL micros (millionths of a dollar, at retail = cost × markup): POSITIVE for a
    /// grant, NEGATIVE for consumption. The household's balance is the sum of these — never stored, always
    /// derived — so a movement can only ever be appended, never edited.</summary>
    public long AmountMicros { get; set; }

    /// <summary>A short human-readable reason ("Welcome grant", or the action a consumption paid for) —
    /// for the ledger view and support ("where did my dollar go?"). Not machine-load-bearing.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
