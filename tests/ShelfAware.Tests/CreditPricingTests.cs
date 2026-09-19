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
    // ⚠️ A PROPERTY, not a static readonly field, and the mutation gate is why. A static field is
    // initialised once per test host process, and Stryker reuses that process across mutants — so the
    // instance every test shared was built before any mutant was active, and NO assertion against it could
    // ever kill a mutant in BillingOptions' own field initialisers. The `UnitNouns[MealPlan] = "meals"`
    // entry survived exactly that way, with a test sitting right here asserting the quote it produces.
    // A fresh instance per read costs nothing and makes the defaults testable at all.
    private static BillingOptions Default => new();

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

    // ------------------------------------------------------------------ priced by the unit

    [Theory]
    [InlineData(1, 1)]     // one dinner still costs a whole credit — an act that ran is never free
    [InlineData(3, 1)]     // exactly one block
    [InlineData(4, 2)]     // rounds UP: a part-used block is a charged block
    [InlineData(7, 3)]     // a week of dinners
    [InlineData(124, 42)]  // the cap: 31 days x 4 meals, the horizon that used to cost a flat 2
    public void A_meal_plan_is_priced_by_the_meal_because_the_household_picks_how_many(int meals, int credits)
    {
        // ⚠️ A flat price on an act whose size the customer chooses is wrong in whichever direction they
        // choose it. At a flat 2 credits, 124 meals — eighteen provider calls, ~$0.20-0.35 — cost $0.02 of
        // intended cost, and a single rerolled week paid the same as a month.
        Assert.Equal(credits, CreditPricing.CreditsFor(Default, ServiceAction.MealPlan, meals));
    }

    [Fact]
    public void Every_other_action_is_priced_per_act_and_asks_for_one()
    {
        // ⚠️ A unit is not a provider ROUND. A chat turn costing one price however many tool rounds it took
        // is the claim's job and always true; units are how much the household ASKED FOR, and for every
        // action but the meal plan the answer is "one of these, please". So the price is the flat price...
        foreach (var action in CreditPricing.MeteredActions.Where(a => a != ServiceAction.MealPlan))
        {
            Assert.Equal(1, CreditPricing.UnitsPerPrice(Default, action));
            Assert.Equal(
                CreditPricing.CreditsFor(Default, action),
                CreditPricing.CreditsFor(Default, action, units: 1));
        }

        // ...and asking for five of them really would cost five, which is why no caller passes a count for
        // an action priced per act. AiActionScopeSiteTests is what holds that; this says what it prevents.
        Assert.Equal(10, CreditPricing.CreditsFor(Default, ServiceAction.ChatTurn, units: 5));
    }

    [Fact]
    public void A_free_action_stays_free_however_much_of_it_was_asked_for() =>
        Assert.Equal(0, CreditPricing.CreditsFor(Default, ServiceAction.TagSuggest, units: 1000));

    [Fact]
    public void A_nonsense_unit_count_is_read_as_one_rather_than_as_nothing()
    {
        // Zero or negative units would otherwise make an act that really ran cost nothing — the direction
        // a pricing hole hides in. It is also what a bug upstream would produce, so it must not be free.
        Assert.Equal(1, CreditPricing.CreditsFor(Default, ServiceAction.MealPlan, units: 0));
        Assert.Equal(1, CreditPricing.CreditsFor(Default, ServiceAction.MealPlan, units: -5));
    }

    [Fact]
    public void A_misconfigured_block_size_never_divides_by_nothing()
    {
        var broken = new BillingOptions();
        broken.UnitsPerPrice[ServiceAction.MealPlan] = 0;

        Assert.Equal(1, CreditPricing.UnitsPerPrice(broken, ServiceAction.MealPlan));
        Assert.Equal(7, CreditPricing.CreditsFor(broken, ServiceAction.MealPlan, units: 7)); // one per meal
    }

    [Theory]
    [InlineData(ServiceAction.ChatTurn, "2")]
    [InlineData(ServiceAction.TagSuggest, "free")]
    [InlineData(ServiceAction.MealPlan, "1 per 3 meals")]
    public void The_price_list_quotes_a_unit_price_as_a_rate(ServiceAction action, string quote) =>
        // ONE definition of how a price reads, because Settings shows it and a household's ledger has to
        // agree with what Settings showed them.
        Assert.Equal(quote, CreditPricing.QuotePrice(Default, action));

    [Fact]
    public void A_unit_priced_action_with_no_noun_quotes_the_bare_number()
    {
        // The noun is display copy, not pricing. Losing it should make the row terser, never wrong.
        var nameless = new BillingOptions();
        nameless.UnitNouns.Remove(ServiceAction.MealPlan);

        Assert.Equal("1", CreditPricing.QuotePrice(nameless, ServiceAction.MealPlan));
    }

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

    // ------------------------------------------------------------------ what a CHARGE reads on the ledger

    [Fact]
    public void A_charge_for_a_unit_priced_act_says_how_many_units_it_bought()
    {
        // ⚠️ Since a plan was priced by the meal, two honest rows can both read "A meal plan" and be -3 and
        // -42. Without the size the household cannot check either against what it asked for, which is the
        // whole promise of an itemised ledger.
        Assert.Equal("A meal plan (124 meals)",
            CreditPricing.DescribeCharge(Default, ServiceAction.MealPlan, units: 124));
    }

    [Theory]
    [InlineData(ServiceAction.MealPlan, 1)]        // a plan of one meal — the size adds nothing
    [InlineData(ServiceAction.MealPlan, 0)]        // and nor does a nonsense one
    [InlineData(ServiceAction.MealReroll, 1)]      // priced per act, so it has no size to state
    [InlineData(ServiceAction.ChatTurn, 5)]        // per act, and the 5 is not the household's to read
    public void A_charge_with_no_size_worth_stating_reads_exactly_as_the_price_list_names_it(
        ServiceAction action, int units) =>
        // ⚠️ The same wording as Describe, deliberately: the price list, the operator's margin table and a
        // household's ledger are one vocabulary, and only the SIZE is ever added on top of it.
        Assert.Equal(CreditPricing.Describe(action), CreditPricing.DescribeCharge(Default, action, units));

    [Fact]
    public void A_charge_falls_back_to_the_bare_name_when_the_unit_has_none()
    {
        // The noun is display copy. Losing it makes the row terser, never wrong — the same degradation
        // QuotePrice makes, because they are two views of one price.
        var nameless = new BillingOptions();
        nameless.UnitNouns.Remove(ServiceAction.MealPlan);

        Assert.Equal("A meal plan", CreditPricing.DescribeCharge(nameless, ServiceAction.MealPlan, units: 124));
    }

    // ---- how a reversal reads (the settlement's half of the ledger) ----

    [Fact]
    public void A_reversal_says_how_much_of_what_was_paid_for_never_came_back()
    {
        // ⚠️ The household's ledger is its own record of where its credits went, so the row putting credits
        // back has to be checkable against the row that took them. A bare "Refund" beside "-42 A meal plan
        // (124 meals)" leaves them doing the subtraction, and the numbers they'd need are exactly the two
        // the engine already has.
        Assert.Equal(
            "A meal plan — refunded, 117 of 124 meals never came back",
            CreditPricing.DescribeReversal(Default, ServiceAction.MealPlan, delivered: 7, asked: 124));
    }

    [Fact]
    public void A_reversal_of_an_act_that_delivered_nothing_says_so_plainly()
    {
        // The common case by far — a call that failed outright — and "0 of 124 meals never came back" is
        // arithmetically true and reads like a bug. Worth its own wording.
        Assert.Equal(
            "A meal plan — refunded, nothing came back",
            CreditPricing.DescribeReversal(Default, ServiceAction.MealPlan, delivered: 0, asked: 124));

        // Same wording for a delivered count that could only be a defect upstream: the row must not
        // start claiming a negative number of meals arrived.
        Assert.Equal(
            "A meal plan — refunded, nothing came back",
            CreditPricing.DescribeReversal(Default, ServiceAction.MealPlan, delivered: -3, asked: 124));
    }

    [Theory]
    [InlineData(ServiceAction.MealReroll)]   // priced per act, so there is no shortfall to count
    [InlineData(ServiceAction.ChatTurn)]
    [InlineData(ServiceAction.ReceiptExtraction)]
    public void A_reversal_of_an_act_priced_whole_just_says_it_was_refunded(ServiceAction action) =>
        // An act priced per act is all-or-nothing: it delivered or it didn't, and a count of units would be
        // stating a size the price list never charged by.
        Assert.Equal(
            $"{CreditPricing.Describe(action)} — refunded",
            CreditPricing.DescribeReversal(Default, action, delivered: 1, asked: 1));

    [Fact]
    public void A_reversal_falls_back_to_the_bare_name_when_the_unit_has_none()
    {
        // Same degradation as DescribeCharge and QuotePrice: the noun is display copy, so losing it makes
        // the row terser and never wrong.
        var nameless = new BillingOptions();
        nameless.UnitNouns.Remove(ServiceAction.MealPlan);

        Assert.Equal(
            "A meal plan — refunded",
            CreditPricing.DescribeReversal(nameless, ServiceAction.MealPlan, delivered: 7, asked: 124));

        var blank = new BillingOptions();
        blank.UnitNouns[ServiceAction.MealPlan] = "   ";

        Assert.Equal(
            "A meal plan — refunded",
            CreditPricing.DescribeReversal(blank, ServiceAction.MealPlan, delivered: 7, asked: 124));
    }

    [Fact]
    public void A_reversal_never_reports_more_delivered_than_was_asked_for()
    {
        // The scope clamps Delivered to Units, so this shouldn't reach here — but the shortfall is
        // subtraction, and an unclamped one would print "-6 of 124 meals never came back" on a household's
        // own financial record. A ledger row is not the place to discover an upstream defect.
        Assert.Equal(
            "A meal plan — refunded, 0 of 124 meals never came back",
            CreditPricing.DescribeReversal(Default, ServiceAction.MealPlan, delivered: 130, asked: 124));
    }
}
