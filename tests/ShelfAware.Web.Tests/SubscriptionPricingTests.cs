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
        // 27.99 ÷ 3.99 = 7.01 months paid, so 4.98 are saved — "about 5 months".
        { 3.99m, 27.99m, 5 },
        // 27.99 ÷ 2.99 = 9.36 months paid, 2.64 saved. The hand-written copy this replaced said "about
        // two months"; 2.64 is nearer three, so the old line was the rounded-down one, not the right one.
        { 2.99m, 27.99m, 3 },
        { 4.00m, 44.00m, 1 },
        { 4.00m, 48.00m, 0 },
    };

    [Theory]
    [MemberData(nameof(MonthsSavedCases))]
    public void The_months_saved_note_rounds_to_the_nearest_month(decimal monthly, decimal annual, int expected) =>
        Assert.Equal(expected, SubscriptionPricing.MonthsSavedFor(monthly, annual));

    public static TheoryData<decimal, decimal> PricePairsTheBadgeAndNoteMustAgreeOn => new()
    {
        { 3.99m, 27.99m },   // today
        { 2.99m, 27.99m },   // the base until 2026-09-22
        { 3.99m, 36.99m },   // the re-strike §8 records as declined
        { 3.99m, 39.99m },
        { 4.99m, 47.99m },   // the voice tier §1 once sketched
        { 10.00m, 60.00m },
        { 1.00m, 11.99m },
    };

    [Theory]
    [MemberData(nameof(PricePairsTheBadgeAndNoteMustAgreeOn))]
    public void The_badge_and_the_note_tell_the_same_story(decimal monthly, decimal annual)
    {
        // ⚠️ The two numbers sit one above the other in the panel, derived from the same two prices, so a
        // reader compares them: flooring 4.98 months to 4 put "saves about 4 months" under "save 41%",
        // and 4/12 is 33%. Whatever the prices are, the months must be what the percentage implies —
        // hence a Theory over pairs rather than one assertion about today's two constants, which is all
        // the first version of this test checked while its comment promised "whatever the prices are".
        // The percentage floors (up to 1 point, ≈0.12 months low) and the months round (up to 0.5), so
        // the honest gap between them is under 0.7 of a month. Flooring the months instead put them
        // 0.92 apart, which is what a reader saw as a contradiction.
        var impliedByPercent = 12m * SubscriptionPricing.SavingPercentFor(monthly, annual) / 100m;
        var months = (decimal)SubscriptionPricing.MonthsSavedFor(monthly, annual);
        Assert.InRange(months, impliedByPercent - 0.7m, impliedByPercent + 0.7m);
    }

    public static TheoryData<decimal, decimal, bool> SavesCases => new()
    {
        { 3.99m, 27.99m, true },
        { 2.99m, 27.99m, true },
        // An annual at exactly twelve months, and one dearer than twelve, are not savings. ⚠️ $39.99
        // against a $2.99 base is an alternative §8 records as considered, so this is a knob someone may
        // turn — the panel asks this before rendering "save -12%" in the green win colour.
        { 4.00m, 48.00m, false },
        { 4.00m, 50.00m, false },
        { 2.99m, 39.99m, false },
        // ⚠️ A saving in percent but not in whole months. 118 against 12 × 10 is 1% off and 0.2 of a
        // month, so gating on the percentage alone put a green "save 1%" badge over "the annual saves
        // about 0 months" — the contradiction this class exists to close, reintroduced by its own guard.
        { 10.00m, 118.00m, false },
    };

    [Theory]
    [MemberData(nameof(SavesCases))]
    public void An_annual_that_saves_nothing_is_not_offered_as_a_saving(
        decimal monthly, decimal annual, bool saves) =>
        Assert.Equal(saves, SubscriptionPricing.SavesFor(monthly, annual));

    [Fact]
    public void Todays_prices_do_save() => Assert.True(SubscriptionPricing.AnnualSaves);

    [Fact]
    public void A_saving_too_small_to_state_in_months_is_not_offered_in_percent_either()
    {
        // The discriminating half of the case above: the percentage IS positive there, so a guard reading
        // only SavingPercentFor would pass this and still contradict itself on screen.
        Assert.Equal(1, SubscriptionPricing.SavingPercentFor(10.00m, 118.00m));
        Assert.Equal(0, SubscriptionPricing.MonthsSavedFor(10.00m, 118.00m));
        Assert.False(SubscriptionPricing.SavesFor(10.00m, 118.00m));
    }

    [Fact]
    public void The_note_says_month_in_the_singular_when_only_one_is_saved()
    {
        // ⚠️ Asserted through SavingNoteFor, not through AnnualSavingNote: today's prices save five, so a
        // test that only reads the constants never evaluates the singular branch and a mutant that always
        // says "months" lives through it — shipping "the annual saves about 1 months" at $4.00/$44.00.
        Assert.Equal("the annual saves about 1 month", SubscriptionPricing.SavingNoteFor(4.00m, 44.00m));
        Assert.Equal("the annual saves about 2 months", SubscriptionPricing.SavingNoteFor(4.00m, 40.00m));
        Assert.Equal("the annual saves about 0 months", SubscriptionPricing.SavingNoteFor(4.00m, 48.00m));
        Assert.Equal(SubscriptionPricing.SavingNoteFor(3.99m, 27.99m), SubscriptionPricing.AnnualSavingNote);
    }

    [Fact]
    public void The_badge_reads_as_the_copy_beside_it() =>
        Assert.Equal($"save {SubscriptionPricing.AnnualSavingPercent}%", SubscriptionPricing.AnnualSavingBadge);

    public static TheoryData<decimal> NonPositivePrices => new() { 0.00m, -1.00m };

    [Theory]
    [MemberData(nameof(NonPositivePrices))]
    public void A_monthly_price_of_zero_or_less_is_refused_rather_than_rendered(decimal monthly)
    {
        // Both derivations divide by it; a zero would throw deep inside the arithmetic and a negative
        // would quietly render a discount that means nothing.
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionPricing.SavingPercentFor(monthly, 27.99m));
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionPricing.MonthsSavedFor(monthly, 27.99m));
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionPricing.SavesFor(monthly, 27.99m));
    }

    [Theory]
    [MemberData(nameof(NonPositivePrices))]
    public void An_annual_price_of_zero_or_less_is_refused_too(decimal annual)
    {
        // ⚠️ The monthly failed loudly from the start and the annual did not, which is the asymmetry that
        // matters: an annual of −$27.99 rendered a green "save 120%" badge over "saves about 15 months",
        // every derivation agreeing with every other and SavesFor returning true. An annual DEARER than
        // twelve months stays legal — that is SavesFor's job, not an argument error.
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionPricing.SavingPercentFor(3.99m, annual));
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionPricing.MonthsSavedFor(3.99m, annual));
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionPricing.SavesFor(3.99m, annual));
        Assert.Throws<ArgumentOutOfRangeException>(() => SubscriptionPricing.SavingNoteFor(3.99m, annual));
    }
}
