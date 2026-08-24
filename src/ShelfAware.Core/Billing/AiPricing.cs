namespace ShelfAware.Core.Billing;

/// <summary>A model's rate, in dollars per MILLION tokens (input and output priced separately). A
/// settable class rather than a record so the config binder can populate it from the "Billing" section.</summary>
public sealed class ModelRate
{
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
}

/// <summary>
/// Every tunable number in the billing math, bound from the <c>"Billing"</c> config section — so pricing,
/// the credit markup, and the welcome-grant size are OPERATOR VARIABLES an admin edits in appsettings,
/// not constants baked into a build (Jordan's requirement). The defaults here are the current published
/// figures, so a deployment that configures nothing still prices correctly; config keys ADD to or
/// OVERRIDE these (the binder merges onto the initialized instance).
/// </summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>Retail markup on credits: retail = cost × this. Default 1.65 (the 65% markup).</summary>
    public decimal CreditMarkup { get; set; } = 1.65m;

    /// <summary>The one-time welcome grant per new household, in dollars OF COST (the doc's "$1 of my
    /// cost"); stored as retail credit = this × <see cref="CreditMarkup"/>.</summary>
    public decimal WelcomeGrantDollars { get; set; } = 1.00m;

    /// <summary>The rate for a model not in <see cref="ModelRates"/> — a visitor's exotic BYOK model, or
    /// one added to config before this table. Deliberately the priciest current tier so an unknown model
    /// OVER-estimates (visible, self-correcting) rather than reads as free (silently eats margin).</summary>
    public ModelRate FallbackRate { get; set; } = new() { InputPerMTok = 5.00m, OutputPerMTok = 25.00m };

    /// <summary>Model id → rate ($/MTok). Seeded with the current pinned/likely models; an operator
    /// overrides a rate or adds a model by setting e.g. <c>Billing:ModelRates:claude-haiku-4-5:InputPerMTok</c>.</summary>
    public Dictionary<string, ModelRate> ModelRates { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-haiku-4-5"] = new() { InputPerMTok = 1.00m, OutputPerMTok = 5.00m },
        ["claude-haiku-4-5-20251001"] = new() { InputPerMTok = 1.00m, OutputPerMTok = 5.00m },
        ["claude-sonnet-4-6"] = new() { InputPerMTok = 3.00m, OutputPerMTok = 15.00m },
        ["claude-opus-4-8"] = new() { InputPerMTok = 5.00m, OutputPerMTok = 25.00m },
    };
}

/// <summary>
/// Turns a call's token counts into a cost in MICROS (integer millionths of a dollar — the unit the
/// usage row and the credit ledger both accumulate; docs/subscription-plan.md §4 mandates integer, not
/// TEXT-decimal, so it rides the race-safe SQL increment). Pure functions that take a
/// <see cref="BillingOptions"/> (Web consumers pass <c>IOptions&lt;BillingOptions&gt;.Value</c>), so the
/// money math stays in Core and unit-tested while every number stays operator-configurable.
///
/// dollars-per-MTok numerically EQUALS micros-per-token (both divide by 1e6), which is why the cost math
/// below is just tokens × rate. Cost is stamped at call time, so a historical row keeps the price it was
/// charged at when a rate later changes — only new calls price at the new rate.
/// </summary>
public static class AiPricing
{
    private const decimal MicrosPerDollar = 1_000_000m;

    /// <summary>The configured rate for a model id, or <see cref="BillingOptions.FallbackRate"/> when it
    /// isn't listed (or is blank — a provider that didn't report one).</summary>
    public static ModelRate RateFor(BillingOptions options, string? model) =>
        !string.IsNullOrWhiteSpace(model) && options.ModelRates.TryGetValue(model, out var rate)
            ? rate
            : options.FallbackRate;

    /// <summary>The COST of one call in micros, rounded to the nearest micro. Token counts below zero
    /// (an under-reporting provider, or a bug) clamp to zero — never a negative cost.</summary>
    public static long CostMicros(BillingOptions options, string? model, long inputTokens, long outputTokens)
    {
        var rate = RateFor(options, model);
        var input = Math.Max(0, inputTokens);
        var output = Math.Max(0, outputTokens);
        var micros = input * rate.InputPerMTok + output * rate.OutputPerMTok;
        return (long)Math.Round(micros, MidpointRounding.AwayFromZero);
    }

    /// <summary>Cost micros → RETAIL micros (what a credit balance decrements): cost × the configured markup.</summary>
    public static long ToRetailMicros(BillingOptions options, long costMicros) =>
        (long)Math.Round(costMicros * options.CreditMarkup, MidpointRounding.AwayFromZero);

    /// <summary>The welcome grant in RETAIL micros: configured cost-dollars × markup, in micros.</summary>
    public static long WelcomeGrantRetailMicros(BillingOptions options) =>
        (long)Math.Round(options.WelcomeGrantDollars * MicrosPerDollar * options.CreditMarkup, MidpointRounding.AwayFromZero);

    /// <summary>Micros → a display string in dollars (e.g. 1_234_500 → "$1.23"), on the current culture.</summary>
    public static string FormatMicros(long micros) => (micros / MicrosPerDollar).ToString("C2");
}
