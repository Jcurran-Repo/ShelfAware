namespace ShelfAware.Core.Billing;

/// <summary>
/// Declares which <see cref="ServiceAction"/> the AI calls made inside it belong to, so the metering layer
/// can charge ONE price for one user-visible act however many provider calls it takes (a chat turn with five
/// tool rounds is still one <see cref="ServiceAction.ChatTurn"/>).
///
/// <para>Every AI service opens one around its whole operation:
/// <c>using var _ = AiActionScope.Begin(ServiceAction.ReceiptExtraction);</c> — one line, no constructor
/// parameter, no DI. That matters because the alternative was threading a charging collaborator through ten
/// service constructors and every test that builds one, to carry a fact that is really a property of the
/// OPERATION rather than of the service.</para>
///
/// <para>⚠️ Ambient (<see cref="AsyncLocal{T}"/>) state, which is worth being uneasy about, so here is
/// exactly what it does and does not promise. It flows DOWN: a scope opened in a method is visible to
/// everything that method awaits, and to nothing above it. Disposal restores the enclosing scope, so nesting
/// works — and a nested action (chat invoking the recipe adapter through a tool) is charged for BOTH,
/// deliberately: the household got a chat turn and an adapt, and the adapt's own provider work is not free
/// because a tool asked for it.</para>
///
/// <para>⚠️ A call made with NO scope OF ITS OWN is not free — see
/// <see cref="CreditPricing.CreditsForCostMicros"/>. An unlabelled AI service falls back to the old
/// cost-denominated charge, which is visible on the balance and in reconciliation, rather than silently
/// costing nothing. Forgetting the line over-charges a little. The qualifier matters: the fallback fires on
/// <see cref="Current"/> being NULL, so an unlabelled call made INSIDE somebody else's open scope reads that
/// scope instead, finds its charge already claimed, and is free. Every AI service is labelled today (a test
/// scans for it), which is what keeps that case off the map — not the fallback.</para>
///
/// <para>⚠️ Two shapes DO leak a scope, and both end in a free call rather than an over-charge, so neither
/// announces itself. (1) <see cref="Begin"/> called from a NON-async method that returns a Task: an async
/// method's synchronous prologue has its <see cref="System.Threading.ExecutionContext"/> restored on return,
/// which is what contains the scope — a plain method has no such prologue, so the scope escapes to the
/// caller and never ends. (2) Fire-and-forget (<c>Task.Run</c>, an unawaited task) started INSIDE a scope:
/// it captures the context and goes on reading a scope whose <c>using</c> has closed.</para>
///
/// <para>Only the FIRST is held by a test: AiActionScopeSiteTests parses every <see cref="Begin"/> site and
/// fails the build unless its enclosing method, local function or lambda is <c>async</c>. The second is
/// written down and nothing more — a scan cannot tell which awaits inside a scope are fire-and-forget. If
/// you start work you do not await from inside a scope, that work is charged to it.</para>
/// </summary>
public sealed class AiActionScope : IDisposable
{
    private static readonly AsyncLocal<AiActionScope?> Ambient = new();

    private readonly AiActionScope? _enclosing;
    private int _claimed;

    private AiActionScope(ServiceAction action, int units)
    {
        Action = action;
        Units = units;
        _enclosing = Ambient.Value;
    }

    /// <summary>What the calls inside this scope are for.</summary>
    public ServiceAction Action { get; }

    /// <summary>How many of the action's OWN units this act covers — meals for a meal plan, 1 for
    /// everything charged per act. Most actions are one thing the household asked for and cost roughly the
    /// same each time; a meal plan is not, because the household picks the horizon, and a flat price on
    /// something whose size the customer chooses is wrong in whichever direction they choose it. See
    /// <see cref="CreditPricing.CreditsFor"/>.</summary>
    public int Units { get; }

    /// <summary>The innermost open scope, or null when an AI call was made outside one.</summary>
    public static AiActionScope? Current => Ambient.Value;

    /// <summary>Open a scope for <paramref name="action"/>. Dispose it (a <c>using</c>) to restore the
    /// enclosing one. <paramref name="units"/> is how many of the action's own units this act covers, and
    /// is 1 for everything priced per act — see <see cref="Units"/>.</summary>
    public static AiActionScope Begin(ServiceAction action, int units = 1)
    {
        var scope = new AiActionScope(action, Math.Max(1, units));
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>Whether this act's one charge has already been taken — so a LATER call in the same act
    /// knows it draws nothing more.
    ///
    /// <para>⚠️ This is what stops the credit gate refusing the second half of an act the household has
    /// already paid for in full. A meal plan charges its whole price on the first of up to eighteen provider
    /// calls; a gate that asked "can they afford this act?" again on call two would refuse every remaining
    /// batch of a plan that was paid for, and the household would receive a fraction of what it bought while
    /// the page reported success. Read it, don't claim with it — <see cref="TryClaimCharge"/> is the only
    /// thing that may take the charge, because only it is atomic.</para></summary>
    public bool ChargeClaimed => Volatile.Read(ref _claimed) == 1;

    /// <summary>Take this scope's ONE charge, atomically — true exactly once per scope, false for every
    /// later call in the same action. This is what makes "one price per user-visible act" true no matter how
    /// many provider round-trips it took, including concurrent ones.
    ///
    /// <para>⚠️ Interlocked, not a read-then-set: a tool-call loop can fan its rounds out in parallel, and
    /// two rounds both seeing "unclaimed" would charge the household twice. That is held by this line rather
    /// than by a test — a racing test was written and removed for killing a non-atomic version only four
    /// runs in six, which is coverage claimed and not held (see AiActionScopeTests). Don't "simplify" it.</para></summary>
    public bool TryClaimCharge() => Interlocked.Exchange(ref _claimed, 1) == 0;

    /// <summary>Give the claim back, so a LATER call in this action can charge instead. Called only when a
    /// claim was taken and then the money write failed.
    ///
    /// <para>⚠️ Without this, a failed ledger write costs the whole action rather than one call: the claim is
    /// taken before the write, so every remaining round of a five-round turn would find it gone and charge
    /// nothing. Claiming first is still right — it is what stops two parallel rounds both charging — so the
    /// fix is to undo the claim on the one path that can take it without spending it.</para></summary>
    public void ReleaseCharge() => Interlocked.Exchange(ref _claimed, 0);

    public void Dispose() => Ambient.Value = _enclosing;
}
