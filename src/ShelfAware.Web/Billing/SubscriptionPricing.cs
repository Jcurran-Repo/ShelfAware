using System.Globalization;

namespace ShelfAware.Web.Billing;

/// <summary>
/// THE one definition of what the Aware subscription costs — and of every fact a surface derives from
/// those two numbers: the display strings the /about reserve catalog and the billing catalog render, the
/// annual saving badge, and the "saves about N months" line beside it.
///
/// ⚠️ It exists because the price was STATED in three places — <see cref="ShelfAware.Web.Wishlist.WishlistTiers"/>,
/// <see cref="BillingCatalog"/>, and a hand-written "save 22%" badge in <c>BillingPanel.razor</c> — under a
/// comment asking the next editor to keep them in step. A prose rule is not a rule: raising the monthly
/// from $2.99 to $3.99 (2026-09-22, Jordan) would have left that badge claiming a 22% annual discount
/// that had quietly become 41%, on the same screen as the two prices it was wrong about. The two
/// constants below are now the only edit a DISPLAYED price change needs. ⚠️ They are not what the
/// provider charges: <see cref="PaymentsOptions.MonthlyPriceId"/>/<c>AnnualPriceId</c> are, and nothing
/// reconciles the two the way <see cref="BillingCatalog.PacksMatchTheAnchor"/> reconciles the packs —
/// docs/subscription-plan.md §6 carries that as a wire-up checklist item. See CLAUDE.md, "one
/// accessible definition".
/// </summary>
public static class SubscriptionPricing
{
    /// <summary>What Aware costs per month (docs/subscription-plan.md §1). Raised $2.99 → $3.99 on
    /// 2026-09-22 (Jordan's call). ⚠️ The never-raise promise in §1 binds an EXISTING subscriber's price,
    /// not the sticker a new one is quoted — changing this number is a decision about who signs up next.</summary>
    public const decimal MonthlyDollars = 3.99m;

    /// <summary>What Aware costs per year (docs/subscription-plan.md §1). ⚠️ Deliberately NOT re-struck
    /// when the monthly rose on 2026-09-22: Jordan held it at $27.99 the next day, so the ~41% the badge
    /// derives is the intended discount and not a number that got left behind. §8 records the $36.99 and
    /// $39.99 alternatives that were weighed and declined.</summary>
    public const decimal AnnualDollars = 27.99m;

    /// <summary>"$3.99/mo" — the monthly price as a surface shows it.</summary>
    public static string MonthlyDisplay => $"{Money(MonthlyDollars)}/mo";

    /// <summary>"$27.99/yr" — the annual price as a surface shows it.</summary>
    public static string AnnualDisplay => $"{Money(AnnualDollars)}/yr";

    /// <summary>Both prices on one line, the form the /about reserve ladder shows.</summary>
    public static string LadderDisplay => $"{MonthlyDisplay} · {AnnualDisplay}";

    /// <summary>What the annual saves against twelve monthly charges, in whole percent.</summary>
    public static int AnnualSavingPercent => SavingPercentFor(MonthlyDollars, AnnualDollars);

    /// <summary>The badge beside the annual option — "save 41%".</summary>
    public static string AnnualSavingBadge => $"save {AnnualSavingPercent}%";

    /// <summary>How many months of the monthly price the annual gives away.</summary>
    public static int AnnualMonthsSaved => MonthsSavedFor(MonthlyDollars, AnnualDollars);

    /// <summary>Whether today's annual saves enough to be worth advertising. False means the panel drops
    /// the badge and the saving clause together rather than rendering "save -12%" in the green win
    /// colour — see <see cref="SavesFor"/> for what "enough" means and why.</summary>
    public static bool AnnualSaves => SavesFor(MonthlyDollars, AnnualDollars);

    /// <summary>The saving clause the panel puts under the two buttons — "the annual saves about 5
    /// months". Only meaningful when <see cref="AnnualSaves"/>.</summary>
    public static string AnnualSavingNote => SavingNoteFor(MonthlyDollars, AnnualDollars);

    /// <summary>The discount an annual buys against twelve monthly charges, in whole percent. ⚠️ Rounded
    /// DOWN, deliberately: a badge that rounds up advertises a saving the buyer does not get. At
    /// $3.99/$27.99 the true saving is 41.5%, so the badge promises 41% and delivers a little more.
    /// Taken as arguments rather than read off the constants so the RULE can be tested apart from
    /// today's prices — the rounding direction is the part that has to keep holding after a price
    /// change, and a test that only reads the constants cannot see it.</summary>
    public static int SavingPercentFor(decimal monthlyDollars, decimal annualDollars) =>
        (int)decimal.Floor((1m - PositiveAnnual(annualDollars) / TwelveMonthsOf(monthlyDollars)) * 100m);

    /// <summary>How many months of the monthly price an annual gives away, to the nearest month. ⚠️ NOT
    /// floored, unlike <see cref="SavingPercentFor"/>: this number is rendered directly beneath that one,
    /// and flooring 4.98 to 4 put "saves about 4 months" under "save 41%" — two numbers derived from the
    /// same two prices telling a reader different stories (4/12 is 33%). The percentage is a promise and
    /// rounds down; this sentence says "about", which is what licenses rounding to the nearest month and
    /// what makes the pair agree.</summary>
    public static int MonthsSavedFor(decimal monthlyDollars, decimal annualDollars) =>
        (int)decimal.Round(
            12m - PositiveAnnual(annualDollars) / Positive(monthlyDollars), MidpointRounding.AwayFromZero);

    /// <summary>The saving clause for a given pair of prices. ⚠️ Parameterized like every other rule
    /// here, and for the same reason: at today's prices the count is five, so a test reading only the
    /// constants never evaluates the singular branch and a mutant that always says "months" survives it.
    /// A later $4.00/$44.00 pair would have shipped "the annual saves about 1 months".</summary>
    public static string SavingNoteFor(decimal monthlyDollars, decimal annualDollars)
    {
        var months = MonthsSavedFor(monthlyDollars, annualDollars);
        return $"the annual saves about {months} {(months == 1 ? "month" : "months")}";
    }

    /// <summary>Whether an annual at this price saves anything worth stating against twelve monthly
    /// charges. ⚠️ The class throws on a non-positive monthly but cannot refuse an annual dearer than
    /// twelve months — $39.99/yr against a $2.99 base is one of the alternatives
    /// docs/subscription-plan.md §8 records as considered, so this is a knob someone may turn, not a case
    /// that cannot happen. The derivations stay honest about it (they go negative rather than clamping to
    /// a flattering zero) and a surface asks this before offering the annual as a saving at all.
    /// <para>⚠️ BOTH units must be positive, not just the percentage. Gating on the percentage alone
    /// reopened the very contradiction this class exists to close: $10.00/mo against $118.00/yr is a 1%
    /// saving and zero whole months, so the panel offered a green "save 1%" badge above "the annual saves
    /// about 0 months". One predicate, so the badge and the note appear together or not at all.</para></summary>
    public static bool SavesFor(decimal monthlyDollars, decimal annualDollars) =>
        SavingPercentFor(monthlyDollars, annualDollars) > 0
        && MonthsSavedFor(monthlyDollars, annualDollars) > 0;

    private static decimal TwelveMonthsOf(decimal monthlyDollars) => Positive(monthlyDollars) * 12m;

    /// <summary>An annual price is a price, so a zero or negative one is a programming error rather than
    /// a number to render. ⚠️ Guarded for the same reason the monthly is, which the first version of this
    /// class missed: an annual of −$27.99 produced a green "save 120%" badge over "saves about 15 months"
    /// beside "Annual — $-27.99/yr", with every derivation agreeing and <see cref="SavesFor"/> true. An
    /// annual DEARER than twelve months is a different thing and stays legal — see
    /// <see cref="SavesFor"/>.</summary>
    private static decimal PositiveAnnual(decimal annualDollars) =>
        annualDollars > 0m
            ? annualDollars
            : throw new ArgumentOutOfRangeException(
                nameof(annualDollars), annualDollars, "The annual price must be greater than zero.");

    /// <summary>Both derivations divide by the monthly price, so a zero or negative one is a programming
    /// error rather than a number to render — fail loudly instead of showing a nonsense discount.</summary>
    private static decimal Positive(decimal monthlyDollars) =>
        monthlyDollars > 0m
            ? monthlyDollars
            : throw new ArgumentOutOfRangeException(
                nameof(monthlyDollars), monthlyDollars, "The monthly price must be greater than zero.");

    /// <summary>A price as dollars and cents. Invariant, because these are US-dollar product decisions
    /// rather than values to localise — a visitor's culture must not restate the price as "3,99".</summary>
    private static string Money(decimal dollars) =>
        "$" + dollars.ToString("0.00", CultureInfo.InvariantCulture);
}
