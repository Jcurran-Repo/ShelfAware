using ShelfAware.Core.Billing;

namespace ShelfAware.Tests;

/// <summary>The pricing catalog's token→micros math: input and output priced separately, both Haiku ids
/// priced the same, an unknown model over-estimating (never free), and no path to a negative cost.</summary>
public class AiPricingTests
{
    [Fact]
    public void Haiku_prices_input_and_output_separately()
    {
        // $1/MTok in, $5/MTok out → micros/token of 1 and 5. 100 in + 50 out = 100 + 250 = 350 micros.
        Assert.Equal(350, AiPricing.CostMicros("claude-haiku-4-5", inputTokens: 100, outputTokens: 50));
        // A whole million of each is exactly $1 + $5 = $6 = 6,000,000 micros.
        Assert.Equal(6_000_000, AiPricing.CostMicros("claude-haiku-4-5", 1_000_000, 1_000_000));
    }

    [Fact]
    public void The_dated_and_undated_haiku_ids_price_the_same()
    {
        Assert.Equal(
            AiPricing.CostMicros("claude-haiku-4-5", 1234, 567),
            AiPricing.CostMicros("claude-haiku-4-5-20251001", 1234, 567));
    }

    [Fact]
    public void An_unknown_model_falls_back_to_the_priciest_tier_never_free()
    {
        // Never zero for a real call: an unknown model over-estimates ($5/$25) rather than eating margin.
        Assert.Equal(AiPricing.Fallback, AiPricing.PriceFor("some-future-model"));
        // 100 in × 5 + 50 out × 25 = 500 + 1250 = 1750 micros.
        Assert.Equal(1750, AiPricing.CostMicros("some-future-model", 100, 50));
    }

    [Fact]
    public void A_blank_or_missing_model_falls_back()
    {
        Assert.Equal(AiPricing.Fallback, AiPricing.PriceFor(null));
        Assert.Equal(AiPricing.Fallback, AiPricing.PriceFor(""));
        Assert.Equal(AiPricing.Fallback, AiPricing.PriceFor("   "));
    }

    [Fact]
    public void Negative_token_counts_never_produce_a_negative_cost()
    {
        // A provider that under-reports (or a bug) must not credit money back.
        Assert.Equal(0, AiPricing.CostMicros("claude-haiku-4-5", -100, -50));
        Assert.Equal(250, AiPricing.CostMicros("claude-haiku-4-5", -100, 50)); // only the output counts
    }
}
