namespace ShelfAware.Web.Auth;

/// <summary>
/// A household's entitlement tier — what its members may do with the HOST's AI keys on a managed
/// deployment. <see cref="Free"/> is the default (limited by the deployment's configured daily caps,
/// and on a public box gated behind a subscription once the billing work lands); <see cref="Founder"/>
/// is the operator's thank-you tier: unlimited AI, still fully recorded.
///
/// Paid tiers (Aware, Sous Chef) will join here when subscriptions ship — see
/// <c>docs/subscription-plan.md</c> — so this enum is deliberately the SEAM, a touch more than phase 1
/// strictly needs. Stored as INTEGER; existing rows default to <see cref="Free"/> (0), so a household
/// created before tiers existed is unchanged.
/// </summary>
public enum HouseholdTier
{
    Free = 0,
    Founder = 1,
}

public static class HouseholdTierExtensions
{
    /// <summary>THE one definition of "exempt from the managed daily caps entirely". Only
    /// <see cref="HouseholdTier.Founder"/> is — and always will be, because a paid tier is
    /// limited-with-an-allowance, not unlimited. Both of the meter's gates (LLM calls/tokens and
    /// voice-session mints) consult this, so "what does Founder mean" has one home rather than a
    /// comparison copied into each gate.</summary>
    public static bool IsUnlimited(this HouseholdTier tier) => tier == HouseholdTier.Founder;
}
