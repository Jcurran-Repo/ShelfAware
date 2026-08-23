namespace ShelfAware.Web.Auth;

/// <summary>A bearer credential for the read-only GraphQL API: it grants a program (a meal-planner
/// script, say) read access to exactly one household's pantry.
///
/// ⚠️ Lives in auth.db (<see cref="AuthDbContext"/>), NOT as an <c>IHouseholdOwned</c> pantry table.
/// The token→household lookup IS the authentication step, so it happens BEFORE any household is known —
/// a pantry query filter would hide the row (you can't query the household-scoped pantry to discover
/// which household the token grants). auth.db is where credentials already live (accounts, invite
/// codes), has no household filter, and so the auth handler can look a token up by hash directly. That
/// is also why this entity does not walk the pantry "full drill".
///
/// The raw secret is shown ONCE at creation and never stored — only its <see cref="TokenHash"/> and a
/// display <see cref="Prefix"/> persist (the GitHub-PAT model). A token is high-entropy random, so a
/// plain SHA-256 is the correct hash: there is no low-entropy guessing surface that would call for a
/// slow password KDF.</summary>
public class ApiToken
{
    public int Id { get; set; }

    /// <summary>The household this token grants read access to — a plain value, no cross-file FK
    /// (the pantry DB and auth.db are separate SQLite files, exactly as pantry rows reference a
    /// household by id).</summary>
    public required string HouseholdId { get; set; }

    /// <summary>The <c>AppUser</c> who minted it (for the audit trail and the "who created this" column).</summary>
    public required string CreatedByUserId { get; set; }

    /// <summary>User-facing label, e.g. "my meal-planner script" — how a person tells their tokens apart.</summary>
    public required string Name { get; set; }

    /// <summary>SHA-256 (hex) of the raw <c>sa_…</c> secret. The secret itself is never stored, logged,
    /// or echoed; the handler hashes the presented value and looks it up by this. Unique-indexed.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The first few characters of the secret (<c>sa_1a2b3c4d</c>), shown in the UI so a person
    /// can identify a token in their list without ever seeing the secret again. Non-secret by design —
    /// it reveals a handful of the 256 random bits, which changes nothing.</summary>
    public required string Prefix { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Stamped on every accepted use, so a person can spot a token that's still in play (or one
    /// that never gets used and can be revoked). Null until first used.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>When the owner revoked it, or null if live. Revocation is immediate and permanent — a
    /// revoked token is never accepted again (there is no un-revoke; mint a new one).</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>When the token stops working, or null for no expiry. An expiry bounds the blast radius of
    /// a leaked token without anyone having to remember to revoke it.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Whether the token would be accepted right now — the ONE reading of "is this credential
    /// live", shared by the auth handler and <see cref="ApiTokenService"/>. A method rather than a
    /// property so EF leaves it alone (it's behaviour, not a column) and so the caller must name the
    /// clock, which is what makes expiry testable without waiting. Matches
    /// <see cref="Household.InviteIsUsable"/>: at-expiry counts as expired (<c>&gt;</c>, not <c>&gt;=</c>).</summary>
    public bool IsUsable(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
