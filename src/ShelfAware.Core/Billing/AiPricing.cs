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

    /// <summary>Retail markup on credits: retail = cost × this. Default 1.65 (the 65% markup). It prices
    /// what a DOLLAR buys, not what an ACTION costs — see <see cref="CreditPricing"/>.</summary>
    public decimal CreditMarkup { get; set; } = 1.65m;

    /// <summary>THE anchor: what one Shelf Aware credit costs JORDAN, in dollars. Default $0.01 (Jordan's
    /// call, 2026-09-19), which retails for $0.0165 at the default markup. Grants, allowances and pack
    /// sizes are all derived from this one number (<see cref="CreditPricing"/>) rather than set
    /// independently, so there is exactly one exchange rate in the system.</summary>
    public decimal CostDollarsPerCredit { get; set; } = 0.01m;

    /// <summary>What each user-visible action costs, in whole credits — the PUBLIC price list (it is shown
    /// in Settings; an abstract unit with a hidden exchange rate is a casino chip). Operator-configurable
    /// like every other number here: <c>Billing:CreditPrices:ChatTurn</c>.
    ///
    /// Opening values are derived from measured cost ranges (docs/remediation-plan.md §7.2), with anything
    /// too cheap to charge a whole credit for set FREE — that is how the fractional-credit problem goes
    /// away, and it makes the ✨ buttons free, which they should be anyway.
    ///
    /// ⚠️ <see cref="ServiceAction.TtsSynthesis"/> and <see cref="ServiceAction.RealtimeMinute"/> are
    /// ESTIMATES, not measurements: neither per-character nor per-minute vendor cost is observable per call
    /// from inside the app, so these two wait on real invoices measured against real usage and get corrected
    /// from that evidence. Every other price here comes from token costs the app already stamps.</summary>
    public Dictionary<ServiceAction, int> CreditPrices { get; set; } = new()
    {
        [ServiceAction.ReceiptExtraction] = 1,
        [ServiceAction.CensusPhoto] = 1,
        [ServiceAction.ChatTurn] = 2,
        [ServiceAction.RecipeSuggest] = 2,
        [ServiceAction.RecipeAdapt] = 2,
        [ServiceAction.RecipeImport] = 2,
        [ServiceAction.MealPlan] = 2,
        [ServiceAction.MealReroll] = 1,
        [ServiceAction.TagSuggest] = 0,
        [ServiceAction.SubstituteSuggest] = 0,
        [ServiceAction.IngredientAlternatives] = 0,
        [ServiceAction.TtsSynthesis] = 3,
        [ServiceAction.RealtimeMinute] = 12,
    };

    /// <summary>The price for a <see cref="ServiceAction"/> missing from <see cref="CreditPrices"/> — an
    /// action added to the enum before the price list, or a key an operator removed. Deliberately the
    /// ordinary paid price rather than zero, for <see cref="FallbackRate"/>'s reason: an unpriced action
    /// should OVER-charge visibly, not read as free.</summary>
    public int CreditsForUnknownAction { get; set; } = 2;

    /// <summary>The one-time welcome grant per new household, in dollars OF COST (the doc's "$1 of my
    /// cost"); posted as credits at the anchor — see <see cref="CreditPricing.WelcomeGrantCredits"/>
    /// ($1.00 ÷ $0.01 = 100 credits on the defaults).</summary>
    public decimal WelcomeGrantDollars { get; set; } = 1.00m;

    /// <summary>The recurring monthly allowance for an Aware subscriber, in dollars OF COST (the doc's
    /// "$1.65 retail = $1.00 cost" monthly grant); posted as credits at the anchor — see
    /// <see cref="CreditPricing.MonthlyAllowanceCredits"/>. Granted lazily per billing period and does NOT
    /// roll over (§4) — distinct from the one-time <see cref="WelcomeGrantDollars"/>, which persists until
    /// spent.</summary>
    public decimal MonthlyAllowanceDollars { get; set; } = 1.00m;

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
/// This is the COST side only — what a call cost Jordan. What the HOUSEHOLD is charged is a
/// <see cref="ServiceAction"/> priced in credits (<see cref="CreditPricing"/>); the two are deliberately
/// separate units, which is what makes margin per service a number you can read rather than discover on an
/// invoice.
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

    /// <summary>Micros → a display string in dollars (e.g. 1_234_500 → "$1.23"), on the current culture.</summary>
    public static string FormatMicros(long micros) => (micros / MicrosPerDollar).ToString("C2");
}
