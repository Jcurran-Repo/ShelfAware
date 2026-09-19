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
    public const string SectionName = "Demo";

    /// <summary>Max host-key LLM calls across ALL households per day. Null = unbounded. (TTS isn't metered:
    /// the managed demo box reads recipes with a free self-hosted Kokoro sidecar, so there's nothing to cap.)</summary>
    public int? DailyGlobalCallLimit { get; set; }

    /// <summary>Log a Warning the moment the day's global call count crosses this — an early "you're
    /// suddenly getting traffic / cost is accruing" signal, well under the hard cap. It lands in the
    /// server's own logs (journald/stdout) and the /admin <em>Demo box usage</em> panel's threshold tile
    /// reads "· reached" while the day's count is at/over it; it is NOT routed to /admin's <em>error log</em>,
    /// which is for Error-level events only — a routine heads-up isn't an error. Null = no alert.</summary>
    public int? AlertThreshold { get; set; }

    /// <summary>⚠️ THE one reading of "is this Demo section internally coherent?", so the startup narration
    /// and any surface that ever reports valve health ask the same question rather than each re-deriving it.
    /// Empty when there is nothing to object to — which is the family / self-host case (nothing configured
    /// is a coherent posture, not a fault), so this never cries wolf on a box that isn't a demo box.
    /// <para>Both objections describe a box whose operator BELIEVES they have a valve and does not: the
    /// alert without the cap gives a heads-up with no bound behind it, and an alert at or above the cap can
    /// never fire before the cap has already closed the box. Each is provable from the two numbers alone.</para></summary>
    public IReadOnlyList<string> ConfigurationObjections()
    {
        var objections = new List<string>();

        if (AlertThreshold is not null && DailyGlobalCallLimit is null)
        {
            objections.Add(
                "Demo:AlertThreshold is set but Demo:DailyGlobalCallLimit is not — this box warns about "
                + "traffic it will never stop. Set a call limit, or remove the threshold.");
        }

        if (AlertThreshold is int alert && DailyGlobalCallLimit is int cap && alert >= cap)
        {
            objections.Add(
                $"Demo:AlertThreshold ({alert}) is at or above Demo:DailyGlobalCallLimit ({cap}) — the "
                + "heads-up can only arrive once the box is already capped. Set the threshold below the limit.");
        }

        return objections;
    }
}
