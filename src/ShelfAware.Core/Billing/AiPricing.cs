namespace ShelfAware.Core.Billing;

/// <summary>What a model costs, in dollars per MILLION tokens (Anthropic first-party API rates —
/// the same figures the pricing skill reports). Input and output are priced separately.</summary>
public sealed record ModelPrice(decimal InputPerMTok, decimal OutputPerMTok);

/// <summary>
/// The pricing catalog: model id → cost. Turns a call's token counts into a cost in MICROS (millionths
/// of a dollar, the integer unit the usage row and the ledger both accumulate — see
/// docs/subscription-plan.md §4: cost is stamped at call time and integer-only, never TEXT-decimal, so
/// it can ride the race-safe SQL increment).
///
/// Pure and provider-agnostic on purpose — it converts tokens to money and nothing else, so it lives in
/// Core and is unit-tested there. Rates are the current published figures; when a rate changes, a
/// historical usage/ledger row keeps the cost it was STAMPED with (that's the whole point of stamping at
/// call time), and only new calls price at the new rate.
/// </summary>
public static class AiPricing
{
    // Micros of a dollar per whole dollar (and per million tokens): 1 dollar = 1_000_000 micros, and a
    // rate is dollars-per-MILLION-tokens, so "dollars per MTok" numerically EQUALS "micros per token"
    // (both divide by 1e6). That identity is why the cost math below is just tokens × rate.
    private const decimal MicrosPerDollar = 1_000_000m;

    /// <summary>Anthropic first-party rates ($/MTok), keyed by the exact model id the app sends. Both the
    /// dated and undated Haiku ids are listed because the app pins the dated one (LlmOptions) while a
    /// visitor's BYOK config may use either. Extend this as the app supports more models.</summary>
    private static readonly IReadOnlyDictionary<string, ModelPrice> Catalog =
        new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-haiku-4-5"] = new(1.00m, 5.00m),
            ["claude-haiku-4-5-20251001"] = new(1.00m, 5.00m),
            ["claude-sonnet-4-6"] = new(3.00m, 15.00m),
            ["claude-opus-4-8"] = new(5.00m, 25.00m),
        };

    /// <summary>The rate charged for a model not in <see cref="Catalog"/> — a visitor's exotic BYOK model,
    /// or one added to config before this table. Deliberately the priciest current tier so an unknown
    /// model OVER-estimates rather than reads as free: an under-count silently eats the host's margin,
    /// while an over-count is visible and self-corrects the moment the model is added to the catalog.</summary>
    public static readonly ModelPrice Fallback = new(5.00m, 25.00m);

    /// <summary>The rate for a model id, or <see cref="Fallback"/> when it isn't in the catalog. A blank
    /// id (a provider that didn't report one) also falls back.</summary>
    public static ModelPrice PriceFor(string? model) =>
        !string.IsNullOrWhiteSpace(model) && Catalog.TryGetValue(model, out var price) ? price : Fallback;

    /// <summary>The cost of one call in MICROS (millionths of a dollar), rounded to the nearest micro.
    /// <paramref name="inputTokens"/>/<paramref name="outputTokens"/> below zero are treated as zero (a
    /// provider that under-reports must never produce a negative cost).</summary>
    public static long CostMicros(string? model, long inputTokens, long outputTokens)
    {
        var price = PriceFor(model);
        var input = Math.Max(0, inputTokens);
        var output = Math.Max(0, outputTokens);
        // dollars/MTok == micros/token (see MicrosPerDollar), so the product is already in micros.
        var micros = input * price.InputPerMTok + output * price.OutputPerMTok;
        return (long)Math.Round(micros, MidpointRounding.AwayFromZero);
    }

    /// <summary>Micros → a display string in dollars (e.g. 1_234_500 → "$1.23"). Two decimal places for
    /// panel readouts; sub-cent costs still round to $0.00 there, which is honest for a single cheap call.</summary>
    public static string FormatMicros(long micros) => (micros / MicrosPerDollar).ToString("C2");
}
