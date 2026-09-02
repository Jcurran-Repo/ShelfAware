using ShelfAware.Web.Auth;
using ShelfAware.Web.Components;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Settings "Subscription &amp; billing" panel (phase 3 step 3). A pure presentation component, so its
/// per-tier rules render in isolation — above all §8's "a Free household is NOT offered credit packs" and
/// "packs are for subscribers", which must hold in the UI, not only at the checkout endpoint's gate.
/// </summary>
public class BillingPanelTests : PageTestContext
{
    private IRenderedComponent<BillingPanel> Render(
        bool enabled = true, bool managed = true, HouseholdTier tier = HouseholdTier.Free,
        bool hasCustomer = false, DateTimeOffset? renewsAt = null, bool cancelAtPeriodEnd = false,
        string? checkout = null) =>
        Render<BillingPanel>(ps => ps
            .Add(p => p.Enabled, enabled)
            .Add(p => p.Managed, managed)
            .Add(p => p.Tier, tier)
            .Add(p => p.HasCustomer, hasCustomer)
            .Add(p => p.RenewsAt, renewsAt)
            .Add(p => p.CancelAtPeriodEnd, cancelAtPeriodEnd)
            .Add(p => p.Checkout, checkout));

    [Fact]
    public void Nothing_renders_when_payments_are_off()
    {
        // Config-gated: a self-host / no-billing box shows no billing surface at all.
        Assert.Equal("", Render(enabled: false).Markup.Trim());
    }

    [Fact]
    public void Nothing_renders_for_a_byok_circuit()
    {
        // BYOK runs on the visitor's own key — there's nothing to sell them.
        Assert.Equal("", Render(managed: false).Markup.Trim());
    }

    [Fact]
    public void A_free_household_is_offered_the_subscription_but_not_credit_packs()
    {
        var cut = Render(tier: HouseholdTier.Free);

        Assert.Contains("/billing/checkout?product=SubscriptionMonthly", cut.Markup);
        Assert.Contains("/billing/checkout?product=SubscriptionAnnual", cut.Markup);
        // §8: a Free household is never offered packs.
        Assert.DoesNotContain("CreditPack", cut.Markup);
        Assert.DoesNotContain("/billing/portal", cut.Markup);
    }

    [Fact]
    public void An_aware_household_is_offered_credit_packs_but_not_the_subscription()
    {
        var cut = Render(tier: HouseholdTier.Aware);

        Assert.Contains("/billing/checkout?product=CreditPack5", cut.Markup);
        Assert.Contains("/billing/checkout?product=CreditPack10", cut.Markup);
        Assert.Contains("/billing/checkout?product=CreditPack20", cut.Markup);
        // Already subscribed — no "subscribe" action.
        Assert.DoesNotContain("product=SubscriptionMonthly", cut.Markup);
    }

    [Fact]
    public void The_manage_billing_link_shows_only_with_a_customer_on_file()
    {
        Assert.Contains("/billing/portal", Render(tier: HouseholdTier.Aware, hasCustomer: true).Markup);
        Assert.DoesNotContain("/billing/portal", Render(tier: HouseholdTier.Aware, hasCustomer: false).Markup);
    }

    [Fact]
    public void A_founder_sees_a_note_and_no_billing_actions()
    {
        var cut = Render(tier: HouseholdTier.Founder);

        Assert.Contains("Founder", cut.Markup);
        Assert.DoesNotContain("/billing/checkout", cut.Markup); // comped — nothing to buy
        Assert.DoesNotContain("/billing/portal", cut.Markup);
    }

    [Fact]
    public void A_success_banner_shows_after_a_subscription_checkout()
    {
        var cut = Render(tier: HouseholdTier.Aware, checkout: "subscribed");

        Assert.Contains("Aware now", cut.Markup);
        Assert.Contains("callout", cut.Markup);
        Assert.Contains("ok", cut.Markup); // the positive (green) variant
    }

    [Fact]
    public void A_cancelled_checkout_reassures_no_charge()
    {
        Assert.Contains("weren't charged", Render(checkout: "cancelled").Markup);
    }

    [Fact]
    public void A_provider_error_shows_a_try_again_banner()
    {
        // The checkout/portal endpoints redirect here (?checkout=error) when the provider throws, rather than
        // 500ing the user — the panel must have something to say for it.
        var cut = Render(tier: HouseholdTier.Aware, checkout: "error");

        Assert.Contains("try again", cut.Markup);
        Assert.Contains("callout", cut.Markup);
        Assert.DoesNotContain("callout ok", cut.Markup); // not the positive (green) variant — it's a failure
    }

    [Fact]
    public void An_aware_household_sees_its_renewal_date()
    {
        var renews = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Contains("Renews on", Render(tier: HouseholdTier.Aware, renewsAt: renews).Markup);
    }

    [Fact]
    public void A_cancelling_plan_says_when_it_ends()
    {
        var renews = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var cut = Render(tier: HouseholdTier.Aware, renewsAt: renews, cancelAtPeriodEnd: true);

        Assert.Contains("ends on", cut.Markup);
        Assert.DoesNotContain("Renews on", cut.Markup); // it's not renewing — don't say it is
    }
}
