using ShelfAware.Core.Billing;

namespace ShelfAware.Tests;

/// <summary>The pricing catalog's token→micros math and the configurable tunables (rates, markup,
/// welcome grant): input and output priced separately, both Haiku ids the same, an unknown model
/// over-estimating (never free), no negative cost — and every number driven by <see cref="BillingOptions"/>
/// so an operator can retune it in config.</summary>
public class AiPricingTests
{
    private static readonly BillingOptions Defaults = new();

    [Fact]
    public void Haiku_prices_input_and_output_separately()
    {
        // $1/MTok in, $5/MTok out → micros/token of 1 and 5. 100 in + 50 out = 100 + 250 = 350 micros.
        Assert.Equal(350, AiPricing.CostMicros(Defaults, "claude-haiku-4-5", inputTokens: 100, outputTokens: 50));
        // A whole million of each is exactly $1 + $5 = $6 = 6,000,000 micros.
        Assert.Equal(6_000_000, AiPricing.CostMicros(Defaults, "claude-haiku-4-5", 1_000_000, 1_000_000));
    }

    [Fact]
    public void The_dated_and_undated_haiku_ids_price_the_same()
    {
        Assert.Equal(
            AiPricing.CostMicros(Defaults, "claude-haiku-4-5", 1234, 567),
            AiPricing.CostMicros(Defaults, "claude-haiku-4-5-20251001", 1234, 567));
    }

    [Fact]
    public void An_unknown_model_falls_back_to_the_priciest_tier_never_free()
    {
        Assert.Equal(Defaults.FallbackRate, AiPricing.RateFor(Defaults, "some-future-model"));
        // 100 in × 5 + 50 out × 25 = 500 + 1250 = 1750 micros — never zero for a real call.
        Assert.Equal(1750, AiPricing.CostMicros(Defaults, "some-future-model", 100, 50));
    }

    [Fact]
    public void A_blank_or_missing_model_falls_back()
    {
        Assert.Equal(Defaults.FallbackRate, AiPricing.RateFor(Defaults, null));
        Assert.Equal(Defaults.FallbackRate, AiPricing.RateFor(Defaults, ""));
        Assert.Equal(Defaults.FallbackRate, AiPricing.RateFor(Defaults, "   "));
    }

    [Fact]
    public void Negative_token_counts_never_produce_a_negative_cost()
    {
        Assert.Equal(0, AiPricing.CostMicros(Defaults, "claude-haiku-4-5", -100, -50));
        Assert.Equal(250, AiPricing.CostMicros(Defaults, "claude-haiku-4-5", -100, 50)); // only output counts
    }

    [Fact]
    public void Retail_is_cost_times_the_configured_markup()
    {
        Assert.Equal(1650, AiPricing.ToRetailMicros(Defaults, 1000)); // 1000 × 1.65 default

        var pricier = new BillingOptions { CreditMarkup = 2.0m };
        Assert.Equal(2000, AiPricing.ToRetailMicros(pricier, 1000)); // the markup is a config variable
    }

    [Fact]
    public void The_welcome_grant_is_configured_cost_dollars_times_markup()
    {
        // $1.00 of cost × 1.65 = $1.65 retail = 1,650,000 micros.
        Assert.Equal(1_650_000, AiPricing.WelcomeGrantRetailMicros(Defaults));

        var generous = new BillingOptions { WelcomeGrantDollars = 2.00m };
        Assert.Equal(3_300_000, AiPricing.WelcomeGrantRetailMicros(generous)); // the grant size is a config variable
    }

    [Fact]
    public void The_monthly_allowance_is_configured_cost_dollars_times_markup()
    {
        // $1.00 of cost × 1.65 = $1.65 retail = 1,650,000 micros (the recurring Aware grant, phase 4a).
        Assert.Equal(1_650_000, AiPricing.MonthlyAllowanceRetailMicros(Defaults));

        var generous = new BillingOptions { MonthlyAllowanceDollars = 3.00m };
        Assert.Equal(4_950_000, AiPricing.MonthlyAllowanceRetailMicros(generous)); // 3 × 1.65, its own config variable
    }

    [Fact]
    public void A_configured_rate_overrides_the_built_in_one()
    {
        var o = new BillingOptions();
        o.ModelRates["claude-haiku-4-5"] = new ModelRate { InputPerMTok = 2.00m, OutputPerMTok = 10.00m };

        // 100 in × 2 + 50 out × 10 = 200 + 500 = 700 micros, not the default 350.
        Assert.Equal(700, AiPricing.CostMicros(o, "claude-haiku-4-5", 100, 50));
    }

    // The seeded rates for the larger tiers, so a mistyped key or price is caught.
    // A FRESH BillingOptions per call, deliberately: the seed dictionary is an instance-field
    // initializer, so a shared static instance is built once and would cache the pre-mutation keys —
    // a mutated key literal is only observed when the initializer re-runs. The fallback is set to a
    // rate distinct from every seeded one so a key that fails to match is caught even for opus, whose
    // own rate equals the DEFAULT fallback (a matched key returns the seed rate; a missed key returns
    // this fallback — 99 — and the two are then distinguishable).
    [Theory]
    [InlineData("claude-haiku-4-5", 1.00, 5.00)]
    [InlineData("claude-haiku-4-5-20251001", 1.00, 5.00)]
    [InlineData("claude-sonnet-4-6", 3.00, 15.00)]
    [InlineData("claude-opus-4-8", 5.00, 25.00)]
    public void The_seeded_model_rates_are_the_published_figures(string model, double input, double output)
    {
        var o = new BillingOptions { FallbackRate = new ModelRate { InputPerMTok = 99m, OutputPerMTok = 99m } };

        var rate = AiPricing.RateFor(o, model);

        Assert.Equal((decimal)input, rate.InputPerMTok);
        Assert.Equal((decimal)output, rate.OutputPerMTok);
    }

    [Fact]
    public void Micros_format_as_rounded_dollars()
    {
        // 1,234,500 micros = $1.2345 -> rounded to two decimals, and divided (not multiplied) by 1e6.
        var formatted = AiPricing.FormatMicros(1_234_500);
        Assert.Contains("1.23", formatted);       // × instead of ÷ would explode the number away from 1.23
        Assert.DoesNotContain("2345", formatted); // "C2" rounds to cents; a general format would keep 1.2345
    }
}
