using ShelfAware.Core.Billing;

namespace ShelfAware.Web.Billing;

/// <summary>Display + credit-value facts for one purchasable product. <see cref="PriceDisplay"/> is UI copy
/// — the real charge is the provider's configured price id (<see cref="PaymentsOptions"/>);
/// <see cref="Credits"/> is what a PACK grants, and 0 for a subscription (whose fee is not a credit — the
/// monthly allowance is granted separately, phase 4).</summary>
public sealed record BillingProductInfo(BillingProduct Product, string DisplayName, string PriceDisplay, long Credits);

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
    // ⚠️ Pack sizes are DERIVED from the anchor — floor(pack dollars ÷ the retail price of a credit), i.e.
    // CreditPricing.PackCredits — not rounded to a marketing number, so no pack quietly carries a better
    // exchange rate than another. They are literals here because a product decision should not move when an
    // operator edits a config rate: somebody bought "303 credits", and that is what they get.
    // ⚠️ But a literal is only honest while it still matches, and BillingCatalogTests pins these against
    // `new BillingOptions()` — the COMPILED defaults, not the configured anchor the charge path reads. So
    // the agreement is enforced against the LIVE options at startup instead: PacksMatchTheAnchor, below,
    // is a ValidateOnStart check. If round numbers (300/600/1200) are wanted later that is a DISCOUNT
    // decision and belongs in the record as one.
    public static readonly BillingProductInfo Pack5 = new(BillingProduct.CreditPack5, "303 credits", "$5", 303);
    public static readonly BillingProductInfo Pack10 = new(BillingProduct.CreditPack10, "606 credits", "$10", 606);
    public static readonly BillingProductInfo Pack20 = new(BillingProduct.CreditPack20, "1,212 credits", "$20", 1_212);

    /// <summary>The credit packs, ascending — the "buy credits" choices offered to a subscriber (§8).</summary>
    public static readonly IReadOnlyList<BillingProductInfo> Packs = [Pack5, Pack10, Pack20];

    /// <summary>What each pack costs in dollars, beside the credits it grants — the pair the anchor has to
    /// reproduce. Kept next to the literals it checks so the two cannot be edited apart.</summary>
    private static readonly (decimal Dollars, long Credits)[] PackDollars =
        [(5m, Pack5.Credits), (10m, Pack10.Credits), (20m, Pack20.Credits)];

    /// <summary>Whether the LIVE billing options still produce the pack sizes above. Checked at startup
    /// (Program.cs <c>ValidateOnStart</c>), which is the point: the literals were derived from the DEFAULT
    /// anchor, so an operator who edits <c>Billing:CostDollarsPerCredit</c> or <c>Billing:CreditMarkup</c>
    /// silently changes what a dollar buys everywhere EXCEPT here, and then sells $5 of credit at a rate
    /// that no longer applies. Tripling the markup leaves $5 granting 303 credits where the anchor now says
    /// 166 — an 82% over-grant the operator eats, agreeing with nothing and failing no test, because the
    /// tests pin these against <c>new BillingOptions()</c> rather than against the configured one.</summary>
    public static bool PacksMatchTheAnchor(BillingOptions options) =>
        PackDollars.All(p => CreditPricing.PackCredits(options, p.Dollars) == p.Credits);

    public static bool IsPack(BillingProduct product) =>
        product is BillingProduct.CreditPack5 or BillingProduct.CreditPack10 or BillingProduct.CreditPack20;

    public static bool IsSubscription(BillingProduct product) =>
        product is BillingProduct.SubscriptionMonthly or BillingProduct.SubscriptionAnnual;

    /// <summary>The credits a purchase of this product grants — a pack's face value, or 0 for a
    /// subscription. The fake checkout uses this to build the webhook's amount.</summary>
    public static long CreditsFor(BillingProduct product) => product switch
    {
        BillingProduct.CreditPack5 => Pack5.Credits,
        BillingProduct.CreditPack10 => Pack10.Credits,
        BillingProduct.CreditPack20 => Pack20.Credits,
        _ => 0,
    };
}
