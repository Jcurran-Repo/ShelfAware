namespace ShelfAware.Web.Auth;

/// <summary>
/// A household's entitlement tier — what its members may do with the HOST's AI keys on a managed
/// deployment. <see cref="Free"/> is the default (limited by the deployment's configured daily caps,
/// and on a public box gated behind a subscription once the billing work lands); <see cref="Aware"/> is
/// the paid subscription tier (metered-with-an-allowance — see below); <see cref="Founder"/> is the
/// operator's thank-you tier: unlimited AI, still fully recorded.
///
/// <see cref="Aware"/> is granted by an active subscription (phase 3 — <c>docs/subscription-plan.md</c>
/// §6), where <see cref="Free"/> is granted by default and <see cref="Founder"/> by the admin. This enum
/// is deliberately the SEAM: under the never-raise rule (§1) a future paid tier can always be ADDED here.
/// (There is no planned voice tier — the old "Sous Chef" was dropped 2026-09-02: the realtime Live agent
/// is hidden and voice folds into Aware.) Stored as INTEGER; existing rows default to <see cref="Free"/>
/// (0), so a household created before tiers existed is unchanged.
/// </summary>
public enum HouseholdTier
{
    Free = 0,
    Founder = 1,

    /// <summary>The paid subscription tier (§1 "Aware"). Deliberately NOT unlimited: it is
    /// metered-with-an-allowance — the monthly grant plus purchased credits (§4) — so it goes through
    /// the same meter as Free rather than bypassing it. <see cref="HouseholdTierExtensions.IsUnlimited"/>
    /// stays Founder-only for exactly this reason. The subscription that grants it, and the credit-
    /// balance enforcement that meters it, land in later phase-3 steps; adding the value now is the
    /// enum SEAM, and it changes no gate today (Aware is not unlimited, so every gate treats it as Free
    /// does until enforcement wires the balance in).</summary>
    Aware = 2,
}

public static class HouseholdTierExtensions
{
    /// <summary>THE one definition of "exempt from the managed daily caps entirely". Only
    /// <see cref="HouseholdTier.Founder"/> is — and always will be, because a paid tier
    /// (<see cref="HouseholdTier.Aware"/>) is limited-with-an-allowance, not unlimited. Both of the
    /// meter's gates (LLM calls/tokens and voice-session mints) consult this, so "what does Founder
    /// mean" has one home rather than a comparison copied into each gate.</summary>
    public static bool IsUnlimited(this HouseholdTier tier) => tier == HouseholdTier.Founder;
}
