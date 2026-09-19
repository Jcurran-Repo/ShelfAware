using ShelfAware.Core.Billing;

namespace ShelfAware.Web.Auth;

/// <summary>
/// One day's reconciliation row for one <see cref="ServiceAction"/>: what the action was CHARGED in credits
/// against what it COST in provider dollars. Box-wide (NOT household-scoped) operator data in auth.db, like
/// <see cref="DemoUsageDay"/> and <c>ErrorLogEntry</c> — aggregated across every household, with no household
/// id stored, so there is no tenancy question to get wrong and nothing here belongs to anyone's export.
///
/// <para>⚠️ This table is the concrete thing the credit buys. Under the old cost-denominated balance, credits
/// and cost were the SAME number, so no arithmetic could tell you a chat turn was underpriced — you would
/// have to leave the model to find out. Charging a price list and recording the cost separately makes margin
/// per service a number an operator READS (docs/remediation-plan.md §7.2d).</para>
///
/// <para><see cref="Action"/> is null for a call made outside an <see cref="AiActionScope"/> — which, now that
/// every AI service opens one, means a service nobody labelled. Its cost still lands here (under "unlabelled")
/// rather than vanishing, because a hole you can see is worth more than a tidy total. NULLs are distinct to
/// SQLite so the unique index does not collapse them; the reader GROUPs, so a handful of unlabelled rows for
/// one day read as one line either way.</para>
/// </summary>
public sealed class ServiceMarginDay
{
    public int Id { get; set; }

    /// <summary>The calendar day (server-local), matching every other daily counter in the app.</summary>
    public DateOnly Day { get; set; }

    /// <summary>The action these calls served, or null for an unlabelled call — see the class remarks.</summary>
    public ServiceAction? Action { get; set; }

    /// <summary>Provider calls attributed to this action today. More than <see cref="Charges"/> whenever an
    /// action takes several round-trips, which is exactly the variance the flat price absorbs.</summary>
    public int Calls { get; set; }

    /// <summary>How many times this action was CHARGED today — one per completed action, not per call.</summary>
    public int Charges { get; set; }

    /// <summary>Credits charged for this action today.</summary>
    public long CreditsCharged { get; set; }

    /// <summary>What those calls cost in provider micros — stamped at call time from the configured rates
    /// (<see cref="AiPricing.CostMicros"/>), so a later rate change never rewrites a past day.
    /// EVERY call, including the ones nobody was billed for.</summary>
    public long CostMicros { get; set; }

    /// <summary>The part of <see cref="CostMicros"/> incurred by calls on a BILLABLE path — a managed box
    /// with billing on, a household, a tier that spends credit. The rest is the operator's own use: a
    /// Founder's calls and a BYOK visitor's cost money (the host's or their own) but bill nobody.
    ///
    /// <para>⚠️ This column exists so "cost per charge" has ONE population. <see cref="CostMicros"/> counts
    /// every call while <see cref="Charges"/> counts only billed actions, so dividing one by the other on a
    /// box where the operator is a Founder reports several times an action's true cost — and that ratio is
    /// the number /admin uses to say whether a price is right. Dividing THIS by <see cref="Charges"/> asks
    /// one question of one population: what did the actions we billed for actually cost us?</para></summary>
    public long BillableCostMicros { get; set; }
}
