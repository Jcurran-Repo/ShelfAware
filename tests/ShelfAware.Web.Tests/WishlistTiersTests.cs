using ShelfAware.Web.Wishlist;

namespace ShelfAware.Web.Tests;

/// <summary>The reserve tier catalog — the pre-launch intent picker. Pins the keys the store validates
/// against, that every tier has display copy, and that the base price still mirrors the plan doc.</summary>
public class WishlistTiersTests
{
    [Fact]
    public void The_catalog_is_the_four_plan_tiers_in_ladder_order_each_with_display_copy()
    {
        Assert.Equal(["shelf", "aware", "souschef", "founder"], WishlistTiers.All.Select(t => t.Key).ToArray());
        Assert.All(WishlistTiers.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Price));
            Assert.False(string.IsNullOrWhiteSpace(t.Blurb));
        });
    }

    [Fact]
    public void Aware_carries_the_plans_base_price_so_the_reserve_cant_silently_drift_from_it()
    {
        // Mirrors docs/subscription-plan.md — if the plan's base price changes, this fails until the
        // reserve catalog is updated to match.
        Assert.Contains("$2.99", WishlistTiers.ByKey("aware")!.Price);
    }

    [Fact]
    public void IsValidKey_accepts_catalog_keys_and_refuses_everything_else()
    {
        Assert.True(WishlistTiers.IsValidKey("shelf"));
        Assert.True(WishlistTiers.IsValidKey("founder"));
        Assert.False(WishlistTiers.IsValidKey("enterprise")); // not a tier
        Assert.False(WishlistTiers.IsValidKey(""));
        Assert.False(WishlistTiers.IsValidKey(null));
    }

    [Fact]
    public void ByKey_returns_the_tier_or_null()
    {
        Assert.Equal("Aware", WishlistTiers.ByKey("aware")!.Name);
        Assert.Null(WishlistTiers.ByKey("nope"));
    }
}
