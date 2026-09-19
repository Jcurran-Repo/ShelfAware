using ShelfAware.Core.Billing;
using ShelfAware.Web.Billing;

namespace ShelfAware.Web.Tests;

/// <summary>The one place a purchasable product's display + credit value lives (§1/§8). Pins the pack face
/// values and the sub-vs-pack classification the checkout gate and the fake webhook both depend on.</summary>
public class BillingCatalogTests
{
    [Theory]
    [InlineData(BillingProduct.CreditPack5, true)]
    [InlineData(BillingProduct.CreditPack10, true)]
    [InlineData(BillingProduct.CreditPack20, true)]
    [InlineData(BillingProduct.SubscriptionMonthly, false)]
    [InlineData(BillingProduct.SubscriptionAnnual, false)]
    public void IsPack_is_true_only_for_packs(BillingProduct product, bool isPack) =>
        Assert.Equal(isPack, BillingCatalog.IsPack(product));

    [Theory]
    [InlineData(BillingProduct.SubscriptionMonthly, true)]
    [InlineData(BillingProduct.SubscriptionAnnual, true)]
    [InlineData(BillingProduct.CreditPack10, false)]
    public void IsSubscription_is_true_only_for_subscriptions(BillingProduct product, bool isSub) =>
        Assert.Equal(isSub, BillingCatalog.IsSubscription(product));

    [Theory]
    [InlineData(BillingProduct.CreditPack5, 303L)]
    [InlineData(BillingProduct.CreditPack10, 606L)]
    [InlineData(BillingProduct.CreditPack20, 1_212L)]
    [InlineData(BillingProduct.SubscriptionMonthly, 0L)] // a subscription fee is not a credit grant
    [InlineData(BillingProduct.SubscriptionAnnual, 0L)]
    public void CreditsFor_gives_the_pack_face_value(BillingProduct product, long credits) =>
        Assert.Equal(credits, BillingCatalog.CreditsFor(product));

    [Theory]
    [InlineData(BillingProduct.CreditPack5, 5)]
    [InlineData(BillingProduct.CreditPack10, 10)]
    [InlineData(BillingProduct.CreditPack20, 20)]
    public void Every_pack_is_exactly_what_its_dollars_buy(BillingProduct product, int dollars)
    {
        // ⚠️ The catalog states the face values as literals — a product decision shouldn't move when an
        // operator edits a config rate — so this is what stops them DRIFTING from the anchor they were
        // derived from. Every pack must carry the same exchange rate: floor(dollars ÷ a credit's retail
        // price). If a pack is ever meant to be cheaper per credit, that is a DISCOUNT, and it belongs in
        // the record as one rather than arriving as a silently failing assertion here.
        Assert.Equal(
            CreditPricing.PackCredits(new BillingOptions(), dollars),
            BillingCatalog.CreditsFor(product));
    }

    [Fact]
    public void Packs_are_the_three_in_ascending_order() =>
        Assert.Equal(
            new[] { BillingProduct.CreditPack5, BillingProduct.CreditPack10, BillingProduct.CreditPack20 },
            BillingCatalog.Packs.Select(p => p.Product));
}
