namespace ShelfAware.Web.Services;

/// <summary>
/// Thrown by <see cref="MeteredChatClient"/> when a MANAGED household may not make an AI call: its credit
/// balance is exhausted and its tier grants no unlimited access (phase 4b enforcement,
/// docs/subscription-plan.md §4). A DISTINCT type — not the daily-cap gate's plain
/// <see cref="InvalidOperationException"/> and not a provider error — so the server-side refusal is
/// unambiguous. ⚠️ The AI surfaces do NOT catch this by type (the AI services fail SOFT, swallowing it
/// before a page could): the honest "out of credits / subscribe" message reaches the user through the
/// PRE-CHECK (<see cref="AiErrorText.BlockedReasonAsync"/>), which asks the same entitlement up front and
/// skips the doomed call. This exception is the enforcement backstop for any path that wasn't pre-checked.
/// BYOK circuits are never gated, so it never fires for a visitor on their own key.
/// </summary>
public sealed class AiCreditsExhaustedException(string? message = null)
    : Exception(message ?? AiErrorText.OutOfCredits);
