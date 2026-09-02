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
    public const string OutOfCredits = "You're out of AI credits for now — add a credit pack in Settings to keep going.";
    public const string NoKey = "AI isn't set up yet — add an API key in Settings to use this (bring your own, or subscribe for managed keys).";

    /// <summary>The pre-call gate for a UI surface: null when this circuit may make an AI call now, otherwise
    /// the reason to SHOW (and skip the attempt). A managed household needs a positive balance or an unlimited
    /// tier (<see cref="IEntitlements.IsAiAllowedAsync"/>, which also runs the lazy allowance); a BYOK/self-host
    /// circuit just needs a key. Enforcement lives in <see cref="MeteredChatClient"/> — this only turns a
    /// refusal into a message and avoids a doomed call.</summary>
    public static async ValueTask<string?> BlockedReasonAsync(
        IEntitlements entitlements, CircuitAiSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Managed)
            return await entitlements.IsAiAllowedAsync(cancellationToken) ? null : OutOfCredits;
        return settings.HasKey ? null : NoKey;
    }
}
