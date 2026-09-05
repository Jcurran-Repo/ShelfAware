namespace ShelfAware.Web.Auth;

/// <summary>The managed demo box's BOX-WIDE daily AI valve (docs/subscription-plan.md §10). Everything is
/// null by default, so the family + self-host boxes (which set no <c>Demo</c> section) are unbounded and
/// completely untouched — these caps only bite on a managed public demo box that configures them.
/// <para>These are DISTINCT from the per-household caps (<c>Llm:DailyCallLimit</c>/<c>DailyTokenLimit</c>,
/// which are fair-per-visitor): under open registration a per-household cap doesn't bound the BOX, so this
/// is the box-wide wallet valve that hands the visitor a polite "come back tomorrow" before the host key's
/// own provider-side spend limit hard-stops.</para></summary>
public sealed class DemoOptions
{
    /// <summary>Max host-key LLM calls across ALL households per day. Null = unbounded. (TTS isn't metered:
    /// the managed demo box reads recipes with a free self-hosted Kokoro sidecar, so there's nothing to cap.)</summary>
    public int? DailyGlobalCallLimit { get; set; }

    /// <summary>Warn the admin (a logged Warning → the error log → /admin) the moment the day's global call
    /// count crosses this — an early "you're suddenly getting traffic / cost is accruing" signal, well under
    /// the hard cap. Null = no alert.</summary>
    public int? AlertThreshold { get; set; }
}
