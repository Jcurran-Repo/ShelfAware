namespace ShelfAware.Web.Auth;

/// <summary>Box-wide (NOT household-scoped) daily AI-usage counters — the managed demo box's wallet valve.
/// Operator data in auth.db, like <c>ErrorLogEntry</c>/<c>WishlistEntry</c>: a public managed box with open
/// registration needs a bound the per-household caps (<c>AiUsageMeter</c>) can't give, since every new
/// household gets its own daily allowance. One row per calendar day, written ONLY when a <c>Demo</c> cap or
/// alert is configured — so the family / self-host box (no <c>Demo</c> config) never accumulates a row.</summary>
public sealed class DemoUsageDay
{
    public int Id { get; set; }

    /// <summary>The calendar day (server-local). Unique — one row per day.</summary>
    public DateOnly Day { get; set; }

    /// <summary>Host-key LLM calls made today across all households (chat + extraction + advisors).</summary>
    public int Calls { get; set; }
}
