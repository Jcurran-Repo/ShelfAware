namespace ShelfAware.Web.Auth;

/// <summary>What kind of ledger movement this is. <see cref="Grant"/> (the welcome grant, or an admin
/// comp) and <see cref="Consumption"/> (an AI call drawing the balance down) are phase 2; <see cref="Purchase"/>
/// (a credit-pack buy, positive) and <see cref="Refund"/> (a reversal, negative — balances may go negative,
/// §4) join with payments (phase 3). <see cref="Allowance"/> (the recurring Aware monthly grant) and
/// <see cref="Expiry"/> (its no-rollover sweep) are phase 4. The enum is
/// extensible — a new kind is additive, and nothing switches on it exhaustively (the balance is a plain sum
/// of <see cref="CreditLedgerEntry.AmountCredits"/>, kind is for display/audit).</summary>
public enum CreditEntryKind
{
    Grant = 0,
    Consumption = 1,
    Purchase = 2,
    Refund = 3,

    /// <summary>The recurring Aware monthly allowance (positive), granted lazily per billing period.
    /// Distinct from <see cref="Grant"/> (welcome/comp, which persists) because it does NOT roll over: an
    /// unspent allowance is swept by an <see cref="Expiry"/> entry at the next period (§4). Consumption
    /// spends the allowance before the persisting money (welcome grant + purchases).</summary>
    Allowance = 4,

    /// <summary>A period-end sweep of an unspent <see cref="Allowance"/> (negative) — no-rollover
    /// enforcement (§4). Its magnitude is exactly the prior allowance's unspent remainder, so the
    /// persisting balance (welcome grant + purchases) is untouched.</summary>
    Expiry = 5,

    /// <summary>Credits handed BACK (positive) for an act that was charged and did not deliver — the model
    /// returned nothing usable, a batch came back empty, the act failed outright. Distinct from
    /// <see cref="Refund"/>, which is negative and reverses a PURCHASE (the household's money went back, so
    /// its credits must too); this one goes the other way. Distinct from <see cref="Grant"/> because a
    /// grant persists across billing periods and this does not: it undoes a draw on whatever it was drawn
    /// from. ⚠️ Which is why it is counted alongside <see cref="Consumption"/> in the unspent-allowance
    /// arithmetic — a reversal that was not counted there would leave an allowance looking more spent than
    /// it is, and the period-end sweep would let the difference persist past its month.</summary>
    Reversal = 6,
}

/// <summary>
/// One movement in a household's credit ledger — the append-only money record (docs/subscription-plan.md
/// §4: "the auth-side LEDGER is THE money record; the pantry AiUsage row is display-only"). Balance is the
/// SUM of <see cref="AmountCredits"/> for a household, so nothing mutates a running total in place (the
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

    /// <summary>Signed SHELF AWARE CREDITS: POSITIVE for a grant, allowance or purchase, NEGATIVE for
    /// consumption, an expiry sweep or a refund. The household's balance is the sum of these — never stored,
    /// always derived — so a movement can only ever be appended, never edited.
    ///
    /// ⚠️ Credits, not dollars, since 2026-09-19. A credit is an abstract unit Shelf Aware issues and prices
    /// per <see cref="ShelfAware.Core.Billing.ServiceAction"/>; what it costs JORDAN varies by service, which
    /// is the whole point (docs/remediation-plan.md §7). The ledger used to be denominated in retail micros —
    /// Jordan's own provider bill — which could not price a realtime voice minute at all and made margin per
    /// service invisible by construction. Rows written before the change were converted once at the anchor;
    /// see <see cref="Data.CreditDenominationMigration"/> and <see cref="LegacyAmountMicros"/>.</summary>
    public long AmountCredits { get; set; }

    /// <summary>HISTORICAL. The retail micros this entry was originally denominated in, kept as the receipt
    /// for the 2026-09-19 conversion to credits — so the arithmetic that produced every converted balance
    /// can be audited rather than taken on trust. Zero on every entry written after the conversion; nothing
    /// reads it but a human. Mapped to the original <c>AmountMicros</c> column, so a migrated database and a
    /// freshly created one still have identical schemas (the parity the AdditiveSchema tests pin) — dropping
    /// a SQLite column is a structural rebuild, and rebuilding the MONEY table to delete an audit trail is
    /// the wrong trade twice over.</summary>
    [System.ComponentModel.DataAnnotations.Schema.Column("AmountMicros")]
    public long LegacyAmountMicros { get; set; }

    /// <summary>A short human-readable reason ("Welcome grant", or the action a consumption paid for) —
    /// for the ledger view and support ("where did my dollar go?"). Not machine-load-bearing.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
