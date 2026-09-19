namespace ShelfAware.Core.Billing;

/// <summary>
/// Declares which <see cref="ServiceAction"/> the AI calls made inside it belong to, so the metering layer
/// can charge ONE price for one user-visible act however many provider calls it takes (a chat turn with five
/// tool rounds is still one <see cref="ServiceAction.ChatTurn"/>).
///
/// <para>Every AI service opens one around its whole operation:
/// <c>await using var _ = AiActionScope.Begin(ServiceAction.ReceiptExtraction);</c> — one line, no constructor
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
public sealed class AiActionScope : IAsyncDisposable
{
    private static readonly AsyncLocal<AiActionScope?> Ambient = new();

    private readonly AiActionScope? _enclosing;
    private int _claimed;
    private Func<int, CancellationToken, Task>? _settle;
    private int _delivered;
    private int _closed;

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

    /// <summary>Open a scope for <paramref name="action"/>. Dispose it (an <c>await using</c>) to settle
    /// what it delivered and restore the enclosing one. <paramref name="units"/> is how many of the action's own units this act covers, and
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
    /// thing that may take the charge, because only it is atomic.</para>
    ///
    /// <para>⚠️ A gate that skips the balance on this trusts <see cref="Current"/> to be the act the call
    /// really belongs to. That holds for the same reason the charge itself does — the scope flows down from
    /// the method that opened it — and fails under the second leak shape in this type's own remarks: work
    /// started and not awaited inside a scope. Nothing does that today, and no scan can prove it.</para></summary>
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

    /// <summary>Whether a charge for this act has LANDED and can still be given back — false for a Founder,
    /// a BYOK visitor, a box with billing off, a free-priced action, and any act whose provider calls all
    /// failed before the money write.</summary>
    public bool HasSettlement => Volatile.Read(ref _settle) is not null;

    /// <summary>Told by the metering layer that a charge of <paramref name="credits"/> has LANDED, and
    /// handed the means to reverse it. <paramref name="settle"/> takes how many of the act's units were
    /// delivered and gives back whatever was charged for the rest.
    ///
    /// <para>⚠️ The money layer lives above this assembly and this type must not learn about ledgers, so the
    /// reversal arrives as a delegate rather than a dependency — the same reasoning that makes the scope
    /// ambient rather than a collaborator threaded through ten service constructors.</para>
    ///
    /// <para>⚠️ Refused, and loudly, when the scope has closed with this settlement STRANDED — nothing will
    /// ever run it, so the household would be charged with the refund already out of reach. Nothing does
    /// this today; it is a guard because the shape that could (a provider call outliving the scope that
    /// started it) is one this type's own remarks describe, and it used to cost only a free call.</para>
    ///
    /// <para>⚠️ A closed scope is NOT by itself the refusal, and the difference is the whole point of the
    /// take-back in the body. <see cref="DisposeAsync"/> can race in between this method's two steps and
    /// carry the settlement off with it, in which case the act closed correctly and there is nothing to
    /// report. Throwing on that too — which this did until 2026-09-19 — makes the operator's "charged but
    /// un-refundable" alarm fire on acts that settled fine, and an alarm an operator cannot trust is worse
    /// than none: they would comp the household a second time.</para></summary>
    /// <param name="credits">What landed. Nothing stores it — the settlement closes over its own copy —
    /// so this is here to put a number on the exception below, which is money stranded out of reach.</param>
    /// <exception cref="InvalidOperationException">The scope closed before this settlement was installed,
    /// so no one will ever run it.</exception>
    public void ChargeRecorded(long credits, Func<int, CancellationToken, Task> settle)
    {
        ArgumentNullException.ThrowIfNull(settle);

        // ⚠️ Install FIRST, then check and take it back — not check-then-install. Checking first leaves a
        // window: DisposeAsync can run entirely between the read and the write, take a null settlement,
        // and close, after which this would attach a callback to a closed scope with no exception and no
        // refund — the silent outcome the guard exists to prevent, reached through the window instead of
        // around it. Installing first means DisposeAsync either takes this settlement (and settles it) or
        // has already closed, in which case the exchange below finds it and we take it back.
        Interlocked.Exchange(ref _settle, settle);
        if (Volatile.Read(ref _closed) == 0) return;

        // ⚠️ The scope closed, and there are two ways that happened — only one of them is a problem.
        // Either DisposeAsync took this settlement on its way out, in which case the act closed with the
        // settlement in hand and whatever was owed has been paid; or it closed before this was installed,
        // in which case nothing will ever run it. Taking it back is what distinguishes them: a non-null
        // means it is still sitting here, so nobody took it.
        //
        // ⚠️ Written as "throw on the harmful case" rather than "return early on the benign one", and the
        // difference is coverage rather than taste. The benign case needs a concurrent DisposeAsync to land
        // between the install above and the close-check beside it — a two-instruction window no
        // deterministic test can open, and a racing test is the shape this file rejected once already (see
        // AiActionScopeTests on TryClaimCharge: a killer that lands four runs in six is coverage claimed
        // and not held). An early `return` there would be a STATEMENT nothing executes, which the mutation
        // gate rightly reports; said this way round the benign case is the absence of a statement. The case
        // is still unreachable in a test — it is a condition's false arm now rather than a dead line, which
        // is honest about where the untested ground is instead of failing the gate over it.
        var stranded = Interlocked.Exchange(ref _settle, null);
        if (stranded is not null)
            throw new InvalidOperationException(
                $"A charge of {credits} credit(s) was recorded against a {Action} act that has already "
                + "closed, so it could never be given back. The call that charged it outlived its scope.");
    }

    /// <summary>Report what this act DELIVERED. Anything it was charged for beyond this comes back when
    /// the scope closes — everything, when nothing arrived.
    ///
    /// <para>⚠️ The default is NOTHING, and every act must say otherwise:
    /// <c>AiActionScopeSiteTests</c> fails the build for a scope-opening method that never calls this. The
    /// default is that way round because the paths that skip it are the ones that threw, and an act that
    /// threw delivered nothing. An act that forgets is caught by the build rather than by a household.</para>
    ///
    /// <para>Why a refund at all, rather than charging later: the charge lands on the FIRST provider call of
    /// an act that may take eighteen, which is what stops two parallel rounds both paying. By the time an
    /// act knows what it delivered, the money has already moved, so handing it back is the only honest
    /// correction left.</para>
    ///
    /// <para>⚠️ Last write wins, and <see cref="Volatile"/> for the same reason <c>_claimed</c> is
    /// interlocked: a site that reported delivery from a continuation on one thread and disposed on another
    /// could otherwise read a stale zero here and refund an act that fully delivered. A batched site wanting
    /// a running total must sum before it calls, not call per batch.</para></summary>
    public void Delivered(int units) => Volatile.Write(ref _delivered, Math.Clamp(units, 0, Units));

    /// <summary>Report that this act got an answer back and read it — WHATEVER that answer said. An
    /// honest "there is no recipe in that photo", "no substitutes for this", "nothing on that shelf" is an
    /// answer: the provider call happened, the household asked a question and got a true reply, and it is
    /// paid for (Jordan, 2026-09-19). What comes back is the act that FAILED — the provider was
    /// unreachable, the reply could not be read, the turn was cancelled — where the household asked and
    /// got nothing.
    ///
    /// <para>⚠️ This exists so that "did it deliver?" is asked in ONE place. It used to be re-derived per
    /// service from the shape of the answer — <c>suggestions.Count &gt; 0</c> here,
    /// <c>adapted is not null</c> there, <c>parsed.Recipe is not null</c> in a third — nine sites each
    /// doing their own arithmetic on a question that has one answer. Two of them were already right by
    /// accident (the receipt extractor and the census reader settle on a successful parse and ignore how
    /// many lines came back), which is exactly the shape this repo keeps paying for: sites that agree
    /// today and drift apart the next time one of them is edited.</para>
    ///
    /// <para>⚠️ For an act of ONE unit. A multi-unit act — a meal plan — knows how many of the things it
    /// was paid for actually arrived, and must say so with <see cref="Delivered"/>; this would claim the
    /// whole horizon on a batch that produced three meals out of twelve. <c>AiActionScopeSiteTests</c>
    /// fails the build for an <c>Answered()</c> inside a method that opens its scope with a
    /// <c>units:</c> argument, because that is a rule prose would not hold.</para></summary>
    public void Answered() => Delivered(Units);

    /// <summary>What this act has reported delivering so far — nothing until it says otherwise.</summary>
    public int UnitsDelivered => Volatile.Read(ref _delivered);

    /// <summary>Close the act: give back whatever was charged for units it never delivered, then restore the
    /// enclosing scope.
    ///
    /// <para>⚠️ <see cref="IAsyncDisposable"/> and NOT <see cref="IDisposable"/>, deliberately. Settling
    /// writes to the ledger, so it has to be awaited — and if a plain <c>Dispose</c> existed, a site written
    /// as <c>using var</c> would compile, restore the scope, and silently skip the refund. Removing it makes
    /// the compiler ask every site, which is the only mechanism that has actually held a conversion in this
    /// codebase; the two that were left optional both drifted within a commit.</para>
    ///
    /// <para>Settles at most once, taken the way <see cref="TryClaimCharge"/> takes the charge: the ledger is
    /// append-only with no idempotency key, so a second reversal would pay the household twice for one act
    /// and nothing downstream could net them. A no-op when nothing was charged, which is every unlimited
    /// tier, every BYOK circuit and every box with billing off.</para>
    ///
    /// <para>⚠️ NOT an <c>async</c> method, and it must not become one. An <c>AsyncLocal</c> written inside
    /// an async method does not flow back to its caller — that is the same "flows DOWN only" property this
    /// type's own remarks describe, and it applies to the restore as much as to the open. Written as
    /// <c>async</c>, the assignment below is lost, the scope survives its act, and the NEXT unlabelled AI
    /// call on that circuit is charged to it. Two tests here caught exactly that. So the restore happens
    /// synchronously, before anything is awaited, and the settlement is handed back as a task for the
    /// caller's <c>await using</c> to await — which also means the scope is restored even when settling
    /// throws, without needing a <c>finally</c> to say so.</para></summary>
    public ValueTask DisposeAsync()
    {
        // ⚠️ The restore runs ONCE, not on every call. It is unconditional-looking for a reason and was
        // written that way first: a second dispose would then reinstate this scope's enclosing one as
        // ambient after that one had itself closed, and the next unlabelled call would be charged to a dead
        // act whose one charge is already claimed — which is to say, charged to nobody. Taking the close
        // the way the charge and the settlement are taken keeps all three honest under the same idiom.
        if (Interlocked.Exchange(ref _closed, 1) == 1) return ValueTask.CompletedTask;

        var settle = Interlocked.Exchange(ref _settle, null);
        var delivered = Volatile.Read(ref _delivered);
        var owed = settle is not null && delivered < Units;

        Ambient.Value = _enclosing;

        return owed ? new ValueTask(settle!(delivered, CancellationToken.None)) : ValueTask.CompletedTask;
    }

    /// <summary>Give the claim back, so a LATER call in this action can charge instead. Called only when a
    /// claim was taken and then the money write failed.
    ///
    /// <para>⚠️ Without this, a failed ledger write costs the whole action rather than one call: the claim is
    /// taken before the write, so every remaining round of a five-round turn would find it gone and charge
    /// nothing. Claiming first is still right — it is what stops two parallel rounds both charging — so the
    /// fix is to undo the claim on the one path that can take it without spending it.</para></summary>
    public void ReleaseCharge() => Interlocked.Exchange(ref _claimed, 0);
}
