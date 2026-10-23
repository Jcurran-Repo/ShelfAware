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

    // The current catalog, whether Settings offers the model or a BYOK visitor types it in, each at its own
    // row rather than the fallback tier ($5/$25, i.e. 30,000,000 micros for a million of each) — a missing
    // row overstated a BYOK call by up
    // to fifty times on /admin. A tenth of a million tokens each way is a tenth of the rate pair summed, in
    // micros — 100K rather than a million so Haiku 5.5's prompt stays inside its standard tier (see below).
    [Theory]
    [InlineData("claude-haiku-5-5", 60_000)]        // ($0.10 + $0.50) / 10
    [InlineData("claude-sonnet-5", 1_200_000)]      // ($2 + $10) / 10
    [InlineData("claude-sonnet-5-5", 1_200_000)]    // ($2 + $10) / 10
    [InlineData("claude-opus-5-5", 2_400_000)]      // ($4 + $20) / 10
    public void The_current_catalog_is_priced_at_its_own_rate(string model, long micros) =>
        // A fresh options object, not the shared static: one built once per class run would hold whichever
        // rate table existed when it was first touched.
        Assert.Equal(micros, AiPricing.CostMicros(new BillingOptions(), model, 100_000, 100_000));

    [Fact]
    public void Haiku_5_5_up_to_100K_prompt_tokens_is_the_standard_rate()
    {
        // 100,000 in × 0.10 + 1,000 out × 0.50 = 10,000 + 500. The boundary itself is still standard: the
        // published card is "prompts up to 100K", so it is ABOVE 100K that changes the price.
        Assert.Equal(10_500, AiPricing.CostMicros(new BillingOptions(), "claude-haiku-5-5", 100_000, 1_000));
    }

    [Fact]
    public void Haiku_5_5_past_100K_prompt_tokens_prices_the_whole_call_at_the_long_rate()
    {
        // ⚠️ The under-stamp this closes: one token past the line and the call used to be recorded at a fifth
        // of what it cost. The long rate covers EVERY token, input and output, not just the excess:
        // 100,001 in × 0.50 + 1,000 out × 2.50 = 50,000.5 + 2,500 = 52,500.5, rounded away from zero.
        Assert.Equal(52_501, AiPricing.CostMicros(new BillingOptions(), "claude-haiku-5-5", 100_001, 1_000));
    }

    [Fact]
    public void A_model_with_one_rate_never_switches_however_long_the_prompt()
    {
        // Sonnet 5.5 has no long-prompt card: a million in + a million out is its plain $2 + $10.
        Assert.Equal(12_000_000, AiPricing.CostMicros(new BillingOptions(), "claude-sonnet-5-5", 1_000_000, 1_000_000));
    }

    [Fact]
    public void An_operator_configured_threshold_is_the_one_that_applies()
    {
        var o = new BillingOptions();
        o.ModelRates["claude-haiku-4-5"] = new ModelRate
        {
            InputPerMTok = 1m, OutputPerMTok = 5m,
            LongPromptAboveTokens = 10, LongPromptInputPerMTok = 2m, LongPromptOutputPerMTok = 10m,
        };

        Assert.Equal(10 + 5, AiPricing.CostMicros(o, "claude-haiku-4-5", 10, 1));   // at the line: standard
        Assert.Equal(22 + 10, AiPricing.CostMicros(o, "claude-haiku-4-5", 11, 1));  // past it: long, both sides
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
