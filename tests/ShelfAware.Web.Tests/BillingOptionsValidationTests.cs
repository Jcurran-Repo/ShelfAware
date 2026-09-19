using ShelfAware.Core.Billing;
using ShelfAware.Web.Billing;

namespace ShelfAware.Web.Tests;

/// <summary>
/// What has to be true of the billing config for this box to start. These rules guard the one write in the
/// app nobody can undo — <see cref="Web.Data.CreditDenominationMigration"/> converts every pre-credit
/// balance once, dividing by the retail price of a credit — so the cost of getting them wrong is a ledger
/// that cannot be put back, and the cost of getting them too BROAD is an app that won't start.
/// </summary>
public class BillingOptionsValidationTests
{
    private static string? Check(BillingOptions o, bool sellsCredits = true) =>
        BillingOptionsValidation.FirstProblem(o, sellsCredits);

    [Fact]
    public void The_shipped_defaults_are_valid_on_every_kind_of_box()
    {
        Assert.Null(Check(new BillingOptions()));
        Assert.Null(Check(new BillingOptions(), sellsCredits: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    public void An_anchor_of_zero_or_less_refuses_to_start(double anchor) =>
        Assert.Contains("CostDollarsPerCredit", Check(new BillingOptions { CostDollarsPerCredit = (decimal)anchor }));

    [Fact]
    public void A_markup_of_zero_refuses_to_start() =>
        Assert.Contains("CreditMarkup", Check(new BillingOptions { CreditMarkup = 0m }));

    [Fact]
    public void A_positive_anchor_that_still_prices_a_credit_at_nothing_refuses_to_start()
    {
        // ⚠️ THE finding. The first version of this rule tested the two INPUTS, which is a narrower
        // statement than the harm: 0.0000001 × 1.65 rounds to zero micros, RetailMicrosPerCredit clamps the
        // divisor to 1, and the one-shot ledger conversion turns a $1.65 grant into 1,650,000 credits —
        // through two "greater than zero" checks that both pass.
        var sneaky = new BillingOptions { CostDollarsPerCredit = 0.0000001m };

        Assert.True(sneaky.CostDollarsPerCredit > 0 && sneaky.CreditMarkup > 0); // both inputs look fine
        Assert.Equal(1, CreditPricing.RetailMicrosPerCredit(sneaky));            // the divisor does not
        Assert.Contains("one micro or less", Check(sneaky));
    }

    [Fact]
    public void A_retuned_anchor_refuses_to_start_where_credits_are_SOLD()
    {
        // The pack literals were derived from the default anchor. Tripling the markup makes a credit retail
        // for $0.03, so $5 should buy 166 — and BillingCatalog would still grant 303, an over-grant the
        // operator eats, agreeing with nothing and failing nothing.
        Assert.Contains("pack sizes", Check(new BillingOptions { CreditMarkup = 3.0m }));
    }

    [Fact]
    public void A_retuned_anchor_is_fine_where_no_credits_are_sold()
    {
        // ⚠️ The family box and the demo box sell no packs. The pack rule as an unconditional startup
        // refusal turned a retuned markup on those boxes into a DEAD APP — no dashboard, no pantry, no
        // receipt upload — over a number they never read. The anchor rules stay absolute because they guard
        // an irreversible write; a drifted pack literal is a pricing mistake, and only on a box that sells.
        Assert.Null(Check(new BillingOptions { CreditMarkup = 3.0m }, sellsCredits: false));
        Assert.Null(Check(new BillingOptions { CostDollarsPerCredit = 0.02m }, sellsCredits: false));
    }

    [Fact]
    public void A_broken_anchor_still_refuses_where_no_credits_are_sold()
    {
        // The narrowing above must not have narrowed the part that matters: a box that sells nothing still
        // has a ledger to convert.
        Assert.NotNull(Check(new BillingOptions { CostDollarsPerCredit = 0m }, sellsCredits: false));
        Assert.NotNull(Check(new BillingOptions { CostDollarsPerCredit = 0.0000001m }, sellsCredits: false));
    }
}
