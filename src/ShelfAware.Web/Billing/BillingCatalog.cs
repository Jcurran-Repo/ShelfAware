namespace ShelfAware.Web.Billing;

/// <summary>Display + credit-value facts for one purchasable product. <see cref="PriceDisplay"/> is UI copy
/// — the real charge is the provider's configured price id (<see cref="PaymentsOptions"/>);
/// <see cref="RetailMicros"/> is the credit a PACK grants (its face value), and 0 for a subscription (whose
/// fee is not a credit — the monthly allowance is granted separately, phase 4).</summary>
public sealed record BillingProductInfo(BillingProduct Product, string DisplayName, string PriceDisplay, long RetailMicros);

/// <summary>
/// THE one place a purchasable product's display + credit-value lives (§1/§8 fixed these numbers). The
/// checkout link carries only the <see cref="BillingProduct"/>; the provider maps it to a price. Kept here,
/// not in config, because these are product decisions rather than per-deployment tuning — and the Settings
/// UI and the fake checkout must agree on them (the UI shows the price; the fake grants the pack's value).
/// </summary>
public static class BillingCatalog
{
    public static readonly BillingProductInfo Monthly = new(BillingProduct.SubscriptionMonthly, "Aware — monthly", "$2.99/mo", 0);
    public static readonly BillingProductInfo Annual = new(BillingProduct.SubscriptionAnnual, "Aware — annual", "$27.99/yr", 0);
    public static readonly BillingProductInfo Pack5 = new(BillingProduct.CreditPack5, "$5 credits", "$5", 5_000_000);
    public static readonly BillingProductInfo Pack10 = new(BillingProduct.CreditPack10, "$10 credits", "$10", 10_000_000);
    public static readonly BillingProductInfo Pack20 = new(BillingProduct.CreditPack20, "$20 credits", "$20", 20_000_000);

    /// <summary>The credit packs, ascending — the "buy credits" choices offered to a subscriber (§8).</summary>
    public static readonly IReadOnlyList<BillingProductInfo> Packs = [Pack5, Pack10, Pack20];

    public static bool IsPack(BillingProduct product) =>
        product is BillingProduct.CreditPack5 or BillingProduct.CreditPack10 or BillingProduct.CreditPack20;

    public static bool IsSubscription(BillingProduct product) =>
        product is BillingProduct.SubscriptionMonthly or BillingProduct.SubscriptionAnnual;

    /// <summary>The retail credit a purchase of this product grants — a pack's face value, or 0 for a
    /// subscription. The fake checkout uses this to build the webhook's amount.</summary>
    public static long RetailMicrosFor(BillingProduct product) => product switch
    {
        BillingProduct.CreditPack5 => Pack5.RetailMicros,
        BillingProduct.CreditPack10 => Pack10.RetailMicros,
        BillingProduct.CreditPack20 => Pack20.RetailMicros,
        _ => 0,
    };
}
