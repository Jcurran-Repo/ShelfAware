namespace ShelfAware.Core.Billing;

/// <summary>
/// The things a household can be charged for, at the granularity a PERSON would recognise — not "an LLM
/// call", because nobody buys LLM calls. One user-visible act is one <see cref="ServiceAction"/>, whatever
/// it costs underneath: a chat turn that needs five tool rounds is still one <see cref="ChatTurn"/>.
///
/// ⚠️ This enum is the reason the credit exists. A chat turn (~7,000 tokens) and a realtime voice minute
/// (zero tokens and sixty seconds of somebody else's per-minute billing) have NOTHING in common as costs,
/// and a balance denominated in Jordan's provider bill can only price the first. The credit is the one unit
/// that prices both — see docs/remediation-plan.md §7.
///
/// Adding a value is additive: an action with no configured price falls back to
/// <see cref="BillingOptions.CreditsForUnknownAction"/> (never free — see <see cref="CreditPricing.CreditsFor"/>).
/// </summary>
public enum ServiceAction
{
    /// <summary>Reading one receipt (however many pages or retries it takes).</summary>
    ReceiptExtraction = 0,

    /// <summary>Reading one census — however many shelf photos the household uploaded for it. The act is
    /// the count they asked for, not each picture.</summary>
    CensusPhoto = 1,

    /// <summary>One chat or voice turn, including every tool round it needs.</summary>
    ChatTurn = 2,

    /// <summary>One "what can I make?" batch of recipe suggestions.</summary>
    RecipeSuggest = 3,

    /// <summary>Rewriting one recipe around what's on hand.</summary>
    RecipeAdapt = 4,

    /// <summary>Reading one recipe out of a photo or a paste.</summary>
    RecipeImport = 5,

    /// <summary>Generating one meal plan — the WHOLE plan, however many batches it takes. A long horizon
    /// is generated several slots at a time, and the household asked for a plan, not for a batch.</summary>
    MealPlan = 6,

    /// <summary>The ✨ tag suggestion on a product or a recipe.</summary>
    TagSuggest = 7,

    /// <summary>The ✨ "also works as" suggestion on a product.</summary>
    SubstituteSuggest = 8,

    /// <summary>Filling one ingredient's swap cloud.</summary>
    IngredientAlternatives = 9,

    /// <summary>Synthesising the audio for one recipe read. A cache hit is not an action and is never
    /// charged — the clip already exists and costs nothing to serve.</summary>
    TtsSynthesis = 10,

    /// <summary>One minute of the realtime (live agent) voice session.</summary>
    RealtimeMinute = 11,

    /// <summary>Swapping ONE meal in an existing plan. Its own action rather than a whole
    /// <see cref="MealPlan"/>: it is one small call, and a ledger line reading "A meal plan" for a swapped
    /// dinner tells the household something that did not happen.</summary>
    MealReroll = 12,
}

/// <summary>
/// The Shelf Aware credit: an abstract unit Shelf Aware issues, priced per <see cref="ServiceAction"/>.
/// What a credit costs JORDAN varies by service, and that variance is the point (docs/remediation-plan.md §7).
///
/// <para><b>The anchor</b>, and there is exactly one: <b>1 credit = <see cref="BillingOptions.CostDollarsPerCredit"/>
/// of cost</b> (default $0.01), which at the configured markup retails for $0.0165. Grants, allowances and
/// pack sizes are all computed from it rather than set independently, so no pack can quietly carry a better
/// exchange rate than another. If round-number packs are wanted later that is a DISCOUNT decision and should
/// be recorded as one, not smuggled in as rounding.</para>
///
/// <para>⚠️ The markup lives in what a DOLLAR buys, never in what an ACTION costs. Consumption is priced in
/// credits off the price list; the 65% margin is expressed once, in <see cref="RetailMicrosPerCredit"/>,
/// which is why <see cref="CreditsForCostMicros"/> has no markup term — it cancels.</para>
///
/// Pure functions taking a <see cref="BillingOptions"/> (Web passes <c>IOptions&lt;BillingOptions&gt;.Value</c>),
/// the same shape as <see cref="AiPricing"/>, so the money math stays in Core and unit-tested while every
/// number stays operator-configurable.
/// </summary>
public static class CreditPricing
{
    private const decimal MicrosPerDollar = 1_000_000m;

    /// <summary>What one <see cref="ServiceAction"/> costs, in whole credits, for an act covering
    /// <paramref name="units"/> of the action's own units (see <see cref="AiActionScope.Units"/>). Almost
    /// everything is priced per act and leaves <paramref name="units"/> at 1; a meal plan is priced per
    /// meal, because the household picks how many.
    ///
    /// <para>An action the operator hasn't priced falls back to
    /// <see cref="BillingOptions.CreditsForUnknownAction"/> — deliberately the ordinary paid price rather
    /// than zero, so a newly added action OVER-charges visibly (a customer complains, which is recoverable)
    /// instead of reading as free (which silently eats margin, the same reasoning as
    /// <see cref="BillingOptions.FallbackRate"/>). A configured NEGATIVE price is clamped to zero: a price
    /// list cannot pay people to use the app.</para>
    ///
    /// <para>Units are billed in whole PRICES, rounded up: at one credit per three meals, a ten-meal plan
    /// is four credits. Rounding down would make a two-meal plan free, and an act that ran is never free
    /// unless its price says so.</para></summary>
    public static int CreditsFor(BillingOptions options, ServiceAction action, int units = 1)
    {
        var credits = options.CreditPrices.TryGetValue(action, out var configured)
            ? configured
            : options.CreditsForUnknownAction;
        credits = Math.Max(0, credits);
        // No early-out for a free action: 0 x any number of blocks is already 0, and a guard no input can
        // make fail is a line the mutation gate has to be told to ignore. The POLICY that free stays free
        // however much was asked for is real and tested; it just falls out of the multiply.
        var per = UnitsPerPrice(options, action);
        var blocks = (int)Math.Ceiling(Math.Max(1, units) / (double)per);
        return credits * blocks;
    }

    /// <summary>How many of an action's units one price covers — 1 unless the operator says otherwise, and
    /// never less, so a misconfigured zero cannot divide by nothing or make every unit its own charge.</summary>
    public static int UnitsPerPrice(BillingOptions options, ServiceAction action) =>
        options.UnitsPerPrice.TryGetValue(action, out var per) ? Math.Max(1, per) : 1;

    /// <summary>How the price list quotes an action: "2" for something charged per act, "1 per 3 meals" for
    /// something charged per unit, "free" for a zero price. ONE definition, because Settings shows it and a
    /// household's ledger has to agree with what Settings showed them.</summary>
    public static string QuotePrice(BillingOptions options, ServiceAction action)
    {
        var price = CreditsFor(options, action);       // one unit's worth
        if (price == 0) return "free";

        var per = UnitsPerPrice(options, action);
        if (per == 1 || !options.UnitNouns.TryGetValue(action, out var noun) || string.IsNullOrWhiteSpace(noun))
            return price.ToString();
        return $"{price} per {per} {noun}";
    }

    /// <summary>How a CHARGE reads on a household's ledger: <see cref="Describe"/> for anything priced per
    /// act, and the same wording with the size for anything priced per unit ("A meal plan (124 meals)").
    ///
    /// <para>⚠️ The size belongs here and not in <see cref="Describe"/>, which the price list and the
    /// operator's margin table also read — those two are about the ACTION and would be wrong to name one
    /// household's plan. But a ledger line is about one act, and since a meal plan was priced by the meal
    /// two rows reading "A meal plan" can honestly be -3 and -42. Without the count the household cannot
    /// check either against what it asked for, which is the whole promise of an itemised ledger.</para></summary>
    public static string DescribeCharge(BillingOptions options, ServiceAction action, int units = 1)
    {
        var described = Describe(action);
        var per = UnitsPerPrice(options, action);
        if (per == 1 || units <= 1 || !options.UnitNouns.TryGetValue(action, out var noun) || string.IsNullOrWhiteSpace(noun))
            return described;
        return $"{described} ({units} {noun})";
    }

    /// <summary>How a REVERSAL reads on a household's ledger: what came back, and why. ⚠️ The ledger is the
    /// household's own record of where its credits went, so a row putting credits back has to say what it
    /// is undoing — a bare "Refund" beside a "-42 A meal plan (124 meals)" leaves them counting.</summary>
    public static string DescribeReversal(BillingOptions options, ServiceAction action, int delivered, int asked)
    {
        var described = Describe(action);
        if (delivered <= 0) return $"{described} — refunded, nothing came back";
        var per = UnitsPerPrice(options, action);
        if (per == 1 || !options.UnitNouns.TryGetValue(action, out var noun) || string.IsNullOrWhiteSpace(noun))
            return $"{described} — refunded";
        var missing = asked - delivered;
        // A shortfall of none is the delivered-nothing case's mirror: "0 of 124 meals never came back" is
        // true, reads like a defect, and states a count worth nothing to the household. Unreachable while
        // the scope clamps Delivered and the meter only reverses a positive give-back, so this is about the
        // row staying readable if either of those ever stops being true.
        if (missing <= 0) return $"{described} — refunded";
        return $"{described} — refunded, {missing} of {asked} {noun} never came back";
    }

    /// <summary>The actions a charge is actually WIRED to — the ones some service opens an
    /// <see cref="AiActionScope"/> for. Exactly the set the public price list may quote, because a price
    /// published for an act nothing charges is a statement the engine does not honour (the repo's
    /// "one prediction, one story" rule, applied to money).
    ///
    /// <para>⚠️ <see cref="ServiceAction.TtsSynthesis"/> and <see cref="ServiceAction.RealtimeMinute"/> are
    /// deliberately ABSENT: neither speech path goes through the metering layer, so both were published at a
    /// price no household could ever be charged. They keep their enum values and their estimated prices in
    /// <see cref="BillingOptions.CreditPrices"/> — the estimates are the pending work, not dead weight — and
    /// they rejoin this set on the day they are wired.</para>
    ///
    /// <para>⚠️ Kept honest by <c>AiActionScopeSiteTests</c>, which PARSES the source for
    /// <c>AiActionScope.Begin(ServiceAction.X)</c> and fails the build when this set and the call sites
    /// disagree in either direction. A hand-maintained list of what the code does is a list that goes
    /// stale; this one cannot.</para></summary>
    public static readonly IReadOnlySet<ServiceAction> MeteredActions = System.Collections.Frozen.FrozenSet.ToFrozenSet(
    [
        ServiceAction.ReceiptExtraction,
        ServiceAction.CensusPhoto,
        ServiceAction.ChatTurn,
        ServiceAction.RecipeSuggest,
        ServiceAction.RecipeAdapt,
        ServiceAction.RecipeImport,
        ServiceAction.MealPlan,
        ServiceAction.MealReroll,
        ServiceAction.TagSuggest,
        ServiceAction.SubstituteSuggest,
        ServiceAction.IngredientAlternatives,
    ]);

    /// <summary>What one credit RETAILS for, in micros: the anchor's cost-dollars × the markup (default
    /// $0.01 × 1.65 = 16,500 micros). This is the ONE exchange rate between credits and money — pack sizes
    /// and the one-time re-denomination of the old retail-micros ledger both read it, so there is nothing
    /// for a second rate to drift from. Never below 1 (a zero or negative anchor would make a credit free
    /// and every division by it meaningless).</summary>
    public static long RetailMicrosPerCredit(BillingOptions options) =>
        Math.Max(1, (long)Math.Round(options.CostDollarsPerCredit * MicrosPerDollar * options.CreditMarkup, MidpointRounding.AwayFromZero));

    /// <summary>How many credits <paramref name="dollars"/> of retail spend buys — a credit PACK's size,
    /// <c>floor(dollars ÷ the retail price of a credit)</c>. Floor, so a pack never grants a fraction of a
    /// credit's worth more than it was paid for.</summary>
    // Stryker disable once Equality : `<= 0` and `< 0` are equivalent here — at exactly zero the else branch
    // computes 0 anyway. The guard is written `<=` because "nothing buys nothing" is the statement meant.
    public static long PackCredits(BillingOptions options, decimal dollars) =>
        dollars <= 0 ? 0 : (long)(dollars * MicrosPerDollar / RetailMicrosPerCredit(options));

    /// <summary>The one-time welcome grant, in credits: the configured cost-dollars at the anchor
    /// (default $1.00 of cost ÷ $0.01 = 100 credits).</summary>
    public static long WelcomeGrantCredits(BillingOptions options) =>
        CostDollarsToCredits(options, options.WelcomeGrantDollars);

    /// <summary>The recurring Aware monthly allowance, in credits — same arithmetic as
    /// <see cref="WelcomeGrantCredits"/>, a distinct no-rollover pool (§4).</summary>
    public static long MonthlyAllowanceCredits(BillingOptions options) =>
        CostDollarsToCredits(options, options.MonthlyAllowanceDollars);

    /// <summary>Dollars OF COST → credits at the anchor, floored. Floor rather than round so a grant is
    /// never worth more cost than it was configured to be.</summary>
    private static long CostDollarsToCredits(BillingOptions options, decimal costDollars)
    {
        // Stryker disable once Equality : `<= 0` and `< 0` are equivalent — at exactly zero the rest of the
        // method divides zero by the anchor and returns 0 regardless.
        if (costDollars <= 0) return 0;
        var perCredit = options.CostDollarsPerCredit;
        // ⚠️ A misconfigured anchor of zero must return nothing, not divide by it: `Billing:CostDollarsPerCredit`
        // is operator config, and a decimal divide by zero here would throw on the grant path at first boot.
        return perCredit <= 0 ? 0 : (long)(costDollars / perCredit);
    }

    /// <summary>A call's raw COST in micros → credits, rounded UP — so any non-zero cost is at least one
    /// credit, and nothing real is ever charged as free. This is the fallback for a metered call that
    /// arrived with no <see cref="ServiceAction"/> attached — an AI service nobody has labelled yet. It
    /// deliberately charges rather than skipping: an unlabelled call reads as the old cost-denominated
    /// behaviour (visible on the balance and in reconciliation) instead of silently becoming free, which is
    /// how a pricing hole hides. Note there is no markup term — a credit is defined as a fixed amount OF
    /// COST, so the markup cancels (see the class remarks).</summary>
    public static long CreditsForCostMicros(BillingOptions options, long costMicros)
    {
        // Stryker disable once Equality : `<= 0` and `< 0` are equivalent — at exactly zero the ceiling of
        // zero is zero, so both paths agree. `<=` states "a call that cost nothing charges nothing".
        if (costMicros <= 0) return 0;
        var perCredit = options.CostDollarsPerCredit * MicrosPerDollar;
        // ⚠️ Same misconfigured-anchor guard as CostDollarsToCredits: a zero anchor charges nothing rather
        // than throwing a DivideByZeroException in the metering tail, after the household already got its answer.
        if (perCredit <= 0) return 0;
        // No minimum-of-one clamp: the ceiling already gives it for every positive cost, and a second
        // guard saying the same thing is a guard no test can make fail.
        return (long)Math.Ceiling(costMicros / perCredit);
    }

    /// <summary>The human name of an action — the ledger's "where did my credits go?" line and the public
    /// price list's row label, from ONE definition so a customer reading their ledger and a customer reading
    /// the price list see the same words for the same thing. An unnamed value falls back to its enum name
    /// rather than to nothing, so a new action is legible before it is worded.</summary>
    public static string Describe(ServiceAction action) => action switch
    {
        ServiceAction.ReceiptExtraction => "Reading a receipt",
        ServiceAction.CensusPhoto => "Counting from a photo",
        ServiceAction.ChatTurn => "A chat or voice turn",
        ServiceAction.RecipeSuggest => "Recipe ideas",
        ServiceAction.RecipeAdapt => "Adapting a recipe",
        ServiceAction.RecipeImport => "Importing a recipe",
        ServiceAction.MealPlan => "A meal plan",
        ServiceAction.MealReroll => "Swapping one meal",
        ServiceAction.TagSuggest => "Suggesting tags",
        ServiceAction.SubstituteSuggest => "Suggesting stand-ins",
        ServiceAction.IngredientAlternatives => "Ingredient swaps",
        ServiceAction.TtsSynthesis => "Reading a recipe aloud",
        ServiceAction.RealtimeMinute => "A minute of live voice",
        _ => action.ToString(),
    };

    /// <summary>Credits → a display string ("100 credits", "1 credit"). Negative balances are possible (a
    /// refund after the credit was spent, §4) and read naturally ("-3 credits").</summary>
    public static string FormatCredits(long credits) =>
        $"{credits:N0} {(credits is 1 or -1 ? "credit" : "credits")}";
}
