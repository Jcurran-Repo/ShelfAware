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
/// <para>⚠️ A call made with NO scope is not free — see <see cref="CreditPricing.CreditsForCostMicros"/>.
/// An unlabelled AI service falls back to the old cost-denominated charge, which is visible on the balance
/// and in reconciliation, rather than silently costing nothing. Forgetting the line over-charges a little;
/// it never opens a hole.</para>
/// </summary>
public sealed class AiActionScope : IDisposable
{
    private static readonly AsyncLocal<AiActionScope?> Ambient = new();

    private readonly AiActionScope? _enclosing;
    private int _claimed;

    private AiActionScope(ServiceAction action)
    {
        Action = action;
        _enclosing = Ambient.Value;
    }

    /// <summary>What the calls inside this scope are for.</summary>
    public ServiceAction Action { get; }

    /// <summary>The innermost open scope, or null when an AI call was made outside one.</summary>
    public static AiActionScope? Current => Ambient.Value;

    /// <summary>Open a scope for <paramref name="action"/>. Dispose it (a <c>using</c>) to restore the
    /// enclosing one.</summary>
    public static AiActionScope Begin(ServiceAction action)
    {
        var scope = new AiActionScope(action);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>Take this scope's ONE charge, atomically — true exactly once per scope, false for every
    /// later call in the same action. This is what makes "one price per user-visible act" true no matter how
    /// many provider round-trips it took, including concurrent ones.
    ///
    /// <para>⚠️ Interlocked, not a read-then-set: a tool-call loop can fan its rounds out in parallel, and
    /// two rounds both seeing "unclaimed" would charge the household twice. That is held by this line rather
    /// than by a test — a racing test was written and removed for killing a non-atomic version only four
    /// runs in six, which is coverage claimed and not held (see AiActionScopeTests). Don't "simplify" it.</para></summary>
    public bool TryClaimCharge() => Interlocked.Exchange(ref _claimed, 1) == 0;

    public void Dispose() => Ambient.Value = _enclosing;
}
