using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

public class HouseholdTierTests
{
    /// <summary>THE decision behind the paid tier: only Founder bypasses the meter. Aware is
    /// metered-with-an-allowance (docs/subscription-plan.md §1), so it must read as NOT unlimited — else
    /// a paying subscriber would get the host's wallet unbounded. Pinned over EVERY value so adding a
    /// tier can't silently make it unlimited, and so the new Aware value is asserted explicitly.</summary>
    [Theory]
    [InlineData(HouseholdTier.Founder, true)]
    [InlineData(HouseholdTier.Free, false)]
    [InlineData(HouseholdTier.Aware, false)]
    public void Only_founder_is_unlimited(HouseholdTier tier, bool unlimited) =>
        Assert.Equal(unlimited, tier.IsUnlimited());
}
