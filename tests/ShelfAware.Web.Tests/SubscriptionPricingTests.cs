using ShelfAware.Web.Billing;
using ShelfAware.Web.Wishlist;

namespace ShelfAware.Web.Tests;

/// <summary>THE record of what Aware costs, and of the rule every surface derives from it. The two
/// literals below are the product decision (docs/subscription-plan.md §1) written down once; every other
/// test here asserts that a surface ASKS for them rather than carrying its own copy.</summary>
public class SubscriptionPricingTests
{
    [Fact]
    public void The_plans_prices_are_what_the_code_charges()
    {
        // ⚠️ These literals are the only place a price is typed outside SubscriptionPricing itself, and
        // that is on purpose: changing the price must break exactly one test, which is the prompt to
        // change docs/subscription-plan.md §1 in the same commit. Raised $2.99 → $3.99 on 2026-09-22.
        Assert.Equal(3.99m, SubscriptionPricing.MonthlyDollars);
        Assert.Equal(27.99m, SubscriptionPricing.AnnualDollars);
    }

    [Fact]
    public void Prices_render_as_dollars_and_cents()
    {
        Assert.Equal("$3.99/mo", SubscriptionPricing.MonthlyDisplay);
        Assert.Equal("$27.99/yr", SubscriptionPricing.AnnualDisplay);
        Assert.Equal("$3.99/mo · $27.99/yr", SubscriptionPricing.LadderDisplay);
    }

    [Fact]
    public void The_billing_catalog_shows_the_one_price_rather_than_its_own_copy()
    {
        Assert.Equal(SubscriptionPricing.MonthlyDisplay, BillingCatalog.Monthly.PriceDisplay);
        Assert.Equal(SubscriptionPricing.AnnualDisplay, BillingCatalog.Annual.PriceDisplay);
    }

    [Fact]
    public void The_reserve_ladder_shows_the_one_price_rather_than_its_own_copy() =>
        Assert.Equal(SubscriptionPricing.LadderDisplay, WishlistTiers.ByKey("aware")!.Price);

    /// <summary>Supplied as <see cref="TheoryData{T1,T2,T3}"/> rather than <c>InlineData</c> so the cases
    /// are real <c>decimal</c>s — money written as a <c>double</c> literal is the wrong type to reason
    /// about a price with, even in a test.</summary>
    public static TheoryData<decimal, decimal, int> SavingPercentCases => new()
    {
        // The prices as they stand: 27.99 against 12 × 3.99 = 47.88 is a 41.5% saving.
        { 3.99m, 27.99m, 41 },
        // The prices as they stood before 2026-09-22: 27.99 against 35.88 is 21.99%, which is the
        // "~22% off" the plan describes — and 21 is what an honest badge claims for it.
        { 2.99m, 27.99m, 21 },
        // An annual priced at exactly twelve months saves nothing, and one dearer than twelve months
        // saves less than nothing — neither may round its way up to a positive badge.
        { 4.00m, 48.00m, 0 },
        { 4.00m, 50.00m, -5 },
    };

    [Theory]
    [MemberData(nameof(SavingPercentCases))]
    public void The_saving_badge_rounds_down_so_it_never_promises_more_than_the_buyer_gets(
        decimal monthly, decimal annual, int expected) =>
        Assert.Equal(expected, SubscriptionPricing.SavingPercentFor(monthly, annual));

    public static TheoryData<decimal, decimal, int> MonthsSavedCases => new()
    {
        // 27.99 ÷ 3.99 = 7.01 months paid, so 4.98 are saved — "about 4 months", not five.
        { 3.99m, 27.99m, 4 },
        // 27.99 ÷ 2.99 = 9.36 months paid, 2.64 saved — the "about two months" the old copy claimed by
        // hand, which is the one thing the hand-written line did get right.
        { 2.99m, 27.99m, 2 },
        { 4.00m, 44.00m, 1 },
        { 4.00m, 48.00m, 0 },
    };

    [Theory]
    [MemberData(nameof(MonthsSavedCases))]
    public void The_months_saved_note_rounds_down_too(decimal monthly, decimal annual, int expected) =>
        Assert.Equal(expected, SubscriptionPricing.MonthsSavedFor(monthly, annual));

    [Fact]
    public void The_note_says_month_in_the_singular_when_only_one_is_saved()
    {
        // Guards the pluralisation independently of today's prices, which save four.
        Assert.Equal("the annual saves about 4 months", SubscriptionPricing.AnnualSavingNote);
        Assert.Equal(1, SubscriptionPricing.MonthsSavedFor(4.00m, 44.00m));
    }

    [Fact]
    public void The_badge_reads_as_the_copy_beside_it() =>
        Assert.Equal($"save {SubscriptionPricing.AnnualSavingPercent}%", SubscriptionPricing.AnnualSavingBadge);

    public static TheoryData<decimal> NonPositiveMonthlyPrices => new() { 0.00m, -1.00m };

    [Theory]
    [MemberData(nameof(NonPositiveMonthlyPrices))]
    public void A_monthly_price_of_zero_or_less_is_refused_rather_than_rendered(decimal monthly)
    {
        // Both derivations divide by it; a zero would throw deep inside the arithmetic and a negative
        // would quietly render a discount that means nothing.
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionPricing.SavingPercentFor(monthly, 27.99m));
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionPricing.MonthsSavedFor(monthly, 27.99m));
    }
}
