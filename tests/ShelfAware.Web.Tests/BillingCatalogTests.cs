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
    [InlineData(BillingProduct.CreditPack5, 5_000_000L)]
    [InlineData(BillingProduct.CreditPack10, 10_000_000L)]
    [InlineData(BillingProduct.CreditPack20, 20_000_000L)]
    [InlineData(BillingProduct.SubscriptionMonthly, 0L)] // a subscription fee is not a credit grant
    [InlineData(BillingProduct.SubscriptionAnnual, 0L)]
    public void RetailMicrosFor_gives_the_pack_face_value(BillingProduct product, long micros) =>
        Assert.Equal(micros, BillingCatalog.RetailMicrosFor(product));

    [Fact]
    public void Packs_are_the_three_in_ascending_order() =>
        Assert.Equal(
            new[] { BillingProduct.CreditPack5, BillingProduct.CreditPack10, BillingProduct.CreditPack20 },
            BillingCatalog.Packs.Select(p => p.Product));
}
