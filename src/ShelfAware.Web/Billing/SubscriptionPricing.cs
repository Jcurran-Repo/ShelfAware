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
/// constants below are now the only edit a price change needs. See CLAUDE.md, "one accessible definition".
/// </summary>
public static class SubscriptionPricing
{
    /// <summary>What Aware costs per month (docs/subscription-plan.md §1). Raised $2.99 → $3.99 on
    /// 2026-09-22 (Jordan's call). ⚠️ The never-raise promise in §1 binds an EXISTING subscriber's price,
    /// not the sticker a new one is quoted — changing this number is a decision about who signs up next.</summary>
    public const decimal MonthlyDollars = 3.99m;

    /// <summary>What Aware costs per year (docs/subscription-plan.md §1).</summary>
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

    /// <summary>The muted line under the two buttons — "the annual saves about 4 months".</summary>
    public static string AnnualSavingNote =>
        $"the annual saves about {AnnualMonthsSaved} {(AnnualMonthsSaved == 1 ? "month" : "months")}";

    /// <summary>The discount an annual buys against twelve monthly charges, in whole percent. ⚠️ Rounded
    /// DOWN, deliberately: a badge that rounds up advertises a saving the buyer does not get. At
    /// $3.99/$27.99 the true saving is 41.5%, so the badge promises 41% and delivers a little more.
    /// Taken as arguments rather than read off the constants so the RULE can be tested apart from
    /// today's prices — the rounding direction is the part that has to keep holding after a price
    /// change, and a test that only reads the constants cannot see it.</summary>
    public static int SavingPercentFor(decimal monthlyDollars, decimal annualDollars) =>
        (int)decimal.Floor((1m - annualDollars / TwelveMonthsOf(monthlyDollars)) * 100m);

    /// <summary>How many whole months of the monthly price an annual gives away — rounded DOWN, for the
    /// same reason as <see cref="SavingPercentFor"/>.</summary>
    public static int MonthsSavedFor(decimal monthlyDollars, decimal annualDollars) =>
        (int)decimal.Floor(12m - annualDollars / Positive(monthlyDollars));

    private static decimal TwelveMonthsOf(decimal monthlyDollars) => Positive(monthlyDollars) * 12m;

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
