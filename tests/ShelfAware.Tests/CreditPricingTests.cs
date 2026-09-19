using System.Globalization;
using ShelfAware.Core.Billing;

namespace ShelfAware.Tests;

/// <summary>
/// The Shelf Aware credit's own definition. The unit exists because a chat turn and a realtime voice
/// minute have nothing in common as COSTS but must be buyable with one thing, so what is pinned here is
/// the arithmetic a household's money runs through: the anchor, the price of an act, and the two
/// directions a dollar figure is converted into credits.
/// </summary>
public class CreditPricingTests
{
    private static readonly BillingOptions Default = new();

    // ------------------------------------------------------------------ the anchor

    [Fact]
    public void A_credit_costs_one_cent_and_sells_for_that_plus_the_markup()
    {
        // ⚠️ THE anchor (Jordan, 2026-09-19). Everything else here is arithmetic over these two numbers,
        // so this is the one assertion that would have to be deliberately changed to move the price of
        // the product — which is exactly what it is for.
        Assert.Equal(0.01m, Default.CostDollarsPerCredit);
        Assert.Equal(16_500, CreditPricing.RetailMicrosPerCredit(Default)); // 1¢ × 1.65 = $0.0165
    }

    [Fact]
    public void The_markup_is_expressed_once_in_what_a_dollar_buys()
    {
        // The markup rides in the PRICE of a credit, never in the charge — a credit is a fixed amount of
        // cost, so raising the markup must sell fewer credits per dollar and leave every action's price
        // alone. If the markup ever reached the charge too it would be applied twice, invisibly.
        var dearer = new BillingOptions { CreditMarkup = 3.30m }; // double the markup

        Assert.Equal(
            CreditPricing.CreditsFor(Default, ServiceAction.ChatTurn),
            CreditPricing.CreditsFor(dearer, ServiceAction.ChatTurn));          // the act costs the same
        Assert.Equal(CreditPricing.PackCredits(Default, 10) / 2, CreditPricing.PackCredits(dearer, 10)); // the dollar buys half
    }

    // ------------------------------------------------------------------ the price of an act

    [Theory]
    [InlineData(ServiceAction.ReceiptExtraction, 1)]
    [InlineData(ServiceAction.CensusPhoto, 1)]
    [InlineData(ServiceAction.ChatTurn, 2)]
    [InlineData(ServiceAction.RecipeSuggest, 2)]
    [InlineData(ServiceAction.TtsSynthesis, 3)]
    [InlineData(ServiceAction.RealtimeMinute, 12)]
    [InlineData(ServiceAction.TagSuggest, 0)]         // rides along with work already paid for
    [InlineData(ServiceAction.SubstituteSuggest, 0)]
    [InlineData(ServiceAction.IngredientAlternatives, 0)]
    public void Each_action_has_its_published_price(ServiceAction action, int credits) =>
        Assert.Equal(credits, CreditPricing.CreditsFor(Default, action));

    [Fact]
    public void An_action_with_no_configured_price_is_not_free()
    {
        // Adding a ServiceAction is additive, and a new one landing at ZERO would be a free service
        // nobody decided to give away — silently, and only discovered from the margin table months later.
        // The fallback errs toward charging; setting a price to 0 stays a deliberate act.
        var noPrices = new BillingOptions { CreditPrices = [] };

        Assert.Equal(noPrices.CreditsForUnknownAction, CreditPricing.CreditsFor(noPrices, ServiceAction.MealPlan));
        Assert.True(CreditPricing.CreditsFor(noPrices, ServiceAction.MealPlan) > 0);
    }

    [Fact]
    public void A_negatively_configured_price_can_never_pay_a_household()
    {
        // A config typo (or a negative left by a bad edit) must not turn an action into a credit GRANT.
        var broken = new BillingOptions { CreditPrices = new() { [ServiceAction.ChatTurn] = -5 } };

        Assert.Equal(0, CreditPricing.CreditsFor(broken, ServiceAction.ChatTurn));
    }

    // ------------------------------------------------------------------ dollars → credits, both directions

    [Theory]
    [InlineData(5, 303)]      // floor($5 ÷ $0.0165)
    [InlineData(10, 606)]
    [InlineData(20, 1_212)]
    public void A_pack_is_what_its_dollars_buy_at_retail(decimal dollars, long credits) =>
        Assert.Equal(credits, CreditPricing.PackCredits(Default, dollars));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_pack_worth_nothing_or_less_buys_nothing(decimal dollars) =>
        // ⚠️ The negative case is the one that matters: without the guard, -$5 computes to -303 credits,
        // and a pack that DEBITED a household would be a refund the checkout never authorised. Unreachable
        // from any caller today, which is exactly why it needs pinning rather than trusting.
        Assert.Equal(0, CreditPricing.PackCredits(Default, dollars));

    // ------------------------------------------------------------------ a misconfigured anchor

    [Fact]
    public void An_anchor_of_zero_charges_and_grants_nothing_instead_of_crashing()
    {
        // `Billing:CostDollarsPerCredit` is operator config, so zero is one bad edit away — and every
        // conversion here divides by it. Without the guards this throws DivideByZeroException: on the
        // GRANT path that is a household failing to be created, and on the CHARGE path it is an exception
        // in the metering tail after the household already had its answer. Both return nothing instead.
        var broken = new BillingOptions { CostDollarsPerCredit = 0m };

        Assert.Equal(0, CreditPricing.WelcomeGrantCredits(broken));
        Assert.Equal(0, CreditPricing.MonthlyAllowanceCredits(broken));
        Assert.Equal(0, CreditPricing.CreditsForCostMicros(broken, 350));
        // A credit's retail price never falls below one micro either, so the pack division stays defined.
        Assert.Equal(1, CreditPricing.RetailMicrosPerCredit(broken));
    }

    [Fact]
    public void A_grant_is_dollars_of_COST_not_of_retail()
    {
        // ⚠️ The two directions are deliberately different, and getting them the same way round is the
        // mistake to guard: a PACK is bought, so its dollars are retail; a GRANT is given, so its dollars
        // are what the house is willing to SPEND. $1 of cost is 100 credits, not the 60 a dollar buys.
        Assert.Equal(100, CreditPricing.WelcomeGrantCredits(Default));
        Assert.Equal(100, CreditPricing.MonthlyAllowanceCredits(Default));
        Assert.NotEqual(CreditPricing.PackCredits(Default, 1m), CreditPricing.WelcomeGrantCredits(Default));
    }

    [Fact]
    public void A_grant_configured_to_nothing_grants_nothing()
    {
        var none = new BillingOptions { WelcomeGrantDollars = 0m, MonthlyAllowanceDollars = 0m };

        Assert.Equal(0, CreditPricing.WelcomeGrantCredits(none));
        Assert.Equal(0, CreditPricing.MonthlyAllowanceCredits(none));
    }

    // ------------------------------------------------------------------ the unlabelled fallback

    [Theory]
    [InlineData(1L, 1L)]          // the floor: any real cost is at least one credit
    [InlineData(350L, 1L)]        // a Haiku chat round
    [InlineData(10_000L, 1L)]     // exactly one credit's worth of cost
    [InlineData(10_001L, 2L)]     // a hair over rounds UP, never down
    [InlineData(95_000L, 10L)]
    public void An_unlabelled_call_is_charged_by_its_cost_rounded_up(long costMicros, long credits) =>
        Assert.Equal(credits, CreditPricing.CreditsForCostMicros(Default, costMicros));

    [Fact]
    public void A_call_that_cost_nothing_charges_nothing()
    {
        // A cached clip, or a provider that reported no usage: there is nothing to bill for, and the
        // minimum-of-one floor must not turn "free" into a charge.
        Assert.Equal(0, CreditPricing.CreditsForCostMicros(Default, 0));
        Assert.Equal(0, CreditPricing.CreditsForCostMicros(Default, -50));
    }

    // Reading the old retail-micros money back into credits is the migration's SQL, and its rounding table
    // is asserted against real SQLite in CreditDenominationMigrationTests — not here against a C# twin of
    // the same arithmetic, which would be a second definition of one rule.

    // ------------------------------------------------------------------ what a person reads

    [Fact]
    public void Every_action_describes_itself_in_words_a_household_would_use()
    {
        // The price list is published to the people who spend on it (Settings), so an action with no
        // description would show a CamelCase enum name to a paying customer. Additive enum, so this is
        // what makes adding a value without its sentence fail here rather than on screen.
        foreach (var action in Enum.GetValues<ServiceAction>())
        {
            var text = CreditPricing.Describe(action);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(action.ToString(), text);
            Assert.Contains(' ', text); // a sentence, not an identifier
        }
    }

    [Theory]
    [InlineData(0L, "0 credits")]
    [InlineData(1L, "1 credit")]
    [InlineData(2L, "2 credits")]
    [InlineData(-1L, "-1 credit")]   // a negative one is still ONE of them
    [InlineData(1_212L, "1,212 credits")]
    public void Credits_are_written_the_way_they_are_read(long credits, string text)
    {
        // ⚠️ The culture is pinned for the assertion, not left to the machine. `N0` groups by the AMBIENT
        // culture, so the separator in "1,212" is the test host's, not the code's — on a de-DE agent this
        // test would fail with "1.212" and blame a change nobody made. (The app itself is English-only, so
        // the ambient culture is en-US everywhere it runs; AiPricing.FormatMicros has the same shape.)
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            Assert.Equal(text, CreditPricing.FormatCredits(credits));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
