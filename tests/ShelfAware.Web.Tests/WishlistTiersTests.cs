using ShelfAware.Web.Wishlist;

namespace ShelfAware.Web.Tests;

/// <summary>The reserve tier catalog — the pre-launch intent picker. Pins the keys the store validates
/// against and that every tier has display copy. The PRICE is no longer this suite's business: the
/// ladder asks <c>SubscriptionPricing</c> for it, and SubscriptionPricingTests pins that it is the same
/// one the billing catalog and the panel show.</summary>
public class WishlistTiersTests
{
    [Fact]
    public void The_catalog_is_the_two_selectable_tiers_in_ladder_order_each_with_display_copy()
    {
        // Founder is deliberately NOT here — it's the operator's gift to grant, never a reservable choice.
        // Sous Chef was removed (2026-09-02): voice folds into Aware and the realtime Live agent is hidden.
        Assert.Equal(["shelf", "aware"], WishlistTiers.All.Select(t => t.Key).ToArray());
        Assert.All(WishlistTiers.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Price));
            Assert.False(string.IsNullOrWhiteSpace(t.Blurb));
        });
    }

    [Fact]
    public void Founder_is_not_a_selectable_tier()
    {
        // It's granted by hand (docs/subscription-plan.md), so it must never appear in the reserve picker
        // nor pass validation — otherwise a tampered form could reserve a tier no one is meant to pick.
        Assert.DoesNotContain(WishlistTiers.All, t => t.Key == "founder");
        Assert.False(WishlistTiers.IsValidKey("founder"));
    }

    [Fact]
    public void IsValidKey_accepts_selectable_tiers_and_refuses_everything_else()
    {
        Assert.True(WishlistTiers.IsValidKey("shelf"));
        Assert.True(WishlistTiers.IsValidKey("aware"));
        Assert.False(WishlistTiers.IsValidKey("souschef")); // removed 2026-09-02 — no longer reservable
        Assert.False(WishlistTiers.IsValidKey("enterprise")); // not a tier
        Assert.False(WishlistTiers.IsValidKey(""));
        Assert.False(WishlistTiers.IsValidKey(null));
    }

    [Fact]
    public void ByKey_returns_the_tier_or_null()
    {
        Assert.Equal("Aware", WishlistTiers.ByKey("aware")!.Name);
        Assert.Null(WishlistTiers.ByKey("founder")); // no longer a catalog tier
        Assert.Null(WishlistTiers.ByKey("nope"));
    }
}
