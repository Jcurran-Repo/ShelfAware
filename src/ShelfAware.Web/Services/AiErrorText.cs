using ShelfAware.Core.Billing;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>
/// THE one place for AI-availability UX (phase 4c): the honest reasons a circuit can't make an AI call, and
/// the pre-call check the surfaces use so they don't attempt a call they already know will be refused.
/// Instead of a vague "not working, try again later": OUT OF CREDITS (managed, balance spent) → top up; or
/// NO AI CONFIGURED (BYOK/self-host with no key) → add one.
///
/// The <see cref="MeteredChatClient"/> gate still ENFORCES the credit balance server-side. This is the
/// SURFACE half — it exists because the AI services fail SOFT (they catch broadly and return an empty/fail
/// result), so the gate's <see cref="AiCreditsExhaustedException"/> is swallowed before a page could catch
/// and read it. Checking up front is both reliable and kinder: no doomed call, and the person is told what
/// THEY can do about it. A genuine mid-call PROVIDER failure (the service reached but errored) is not a
/// pre-checkable state — each surface keeps its own context-specific "try again" message for that.
/// </summary>
public static class AiErrorText
{
    // Aware: has a subscription, so packs are the way to keep going. Only shown to an Aware household — a
    // Free one can't buy packs (they're subscribers-only), so it gets SubscribeToUse instead.
    public const string OutOfCredits = "You're out of AI credits for now — add a credit pack in Settings to keep going.";
    public const string SubscribeToUse = "You've used up the free AI trial — subscribe in Settings to keep using it.";
    public const string NoKey = "AI isn't set up yet — add an API key in Settings to use this (bring your own, or subscribe for managed keys).";

    /// <summary>What this act needs against what the household holds — shown when the balance covers
    /// something, just not this. ⚠️ A separate message because "you're out of credits" is FALSE to a
    /// household holding 41 of the 42 a month-long meal plan costs, and the advice is wrong too: the thing
    /// to do is shorten the plan or top up, not conclude the product stopped working. A Free household is
    /// pointed at a subscription rather than a pack, because packs are subscribers-only.</summary>
    public static string NotEnoughCredits(AiAllowance allowance, HouseholdTier tier) =>
        $"This one needs {CreditPricing.FormatCredits(allowance.CreditsNeeded)} and you have "
        + $"{CreditPricing.FormatCredits(allowance.BalanceCredits)} — "
        + (tier == HouseholdTier.Aware
            ? "add a credit pack in Settings, or ask for something smaller."
            : "subscribe in Settings, or ask for something smaller.");

    /// <summary>The pre-call gate for a UI surface: null when this circuit may run <paramref name="act"/>
    /// now, otherwise the reason to SHOW (and skip the attempt). A managed household is blocked when one of
    /// the managed-mode caps has hit for today (<see cref="IManagedCallCaps"/>: the household's own daily
    /// allowance, then the box-wide demo valve — no-ops where neither is configured), else allowed when
    /// <see cref="IEntitlements.CheckAiAsync"/> says the balance covers the
    /// act (which also runs the lazy allowance); when it does not, the next step is tier-specific — a
    /// balance that covers something but not this wants shortening or topping up, a spent Free trial wants
    /// a subscription, a spent Aware balance wants a pack. A BYOK/self-host circuit just needs a key.
    ///
    /// <para>⚠️ <paramref name="act"/> is REQUIRED, and that is the point. It was added as an optional
    /// credit count behind the cancellation token, which asked nothing of the fourteen call sites: thirteen
    /// kept the old one-credit question while the enforcement gate had moved to the act's real price, so a
    /// household with one credit was waved through here and refused there — and told the assistant was
    /// broken. A required parameter makes the compiler ask every site what it is about to run. Enforcement
    /// still lives in <see cref="MeteredChatClient"/>; this only turns a refusal into a message and avoids
    /// a doomed call, and the two now ask <see cref="IEntitlements.CheckAiAsync"/> the same question.</para></summary>
    public static async ValueTask<string?> BlockedReasonAsync(
        IEntitlements entitlements, CircuitAiSettings settings, IManagedCallCaps demoValve,
        ServiceAction act, int units = 1, CancellationToken cancellationToken = default)
    {
        if (settings.Managed)
        {
            // The managed-mode caps first — the per-household daily allowance, then the box-wide demo valve —
            // in the same position and order the MeteredChatClient gate checks them, so the pre-check and the
            // server-side gate agree on this reason. On a family box both are no-ops. (Until 2026-10-07 only
            // the box-wide valve was mirrored here, so a household that had spent its own allowance was shown
            // the surface's generic "try again" — and retrying was the one thing that could not help.)
            if (await demoValve.CallBlockedMessageAsync(cancellationToken) is { } demoBlocked) return demoBlocked;

            var allowance = await entitlements.CheckAiAsync(act, units, cancellationToken);
            if (allowance.Allowed) return null;

            var tier = await entitlements.GetTierAsync(cancellationToken);
            // Blocked on a billing-enabled managed box: name the act the household can actually take.
            if (allowance.ShortForThisAct) return NotEnoughCredits(allowance, tier);
            return tier == HouseholdTier.Aware ? OutOfCredits : SubscribeToUse;
        }
        return settings.HasKey ? null : NoKey;
    }
}
