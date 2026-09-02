namespace ShelfAware.Web.Services;

/// <summary>
/// Thrown by <see cref="MeteredChatClient"/> when a MANAGED household may not make an AI call: its credit
/// balance is exhausted and its tier grants no unlimited access (phase 4b enforcement,
/// docs/subscription-plan.md §4). A DISTINCT type — not the daily-cap gate's plain
/// <see cref="InvalidOperationException"/> and not a provider error — so the AI surfaces can show the right
/// thing ("you're out of credits — top up in Settings") rather than "something went wrong, try again"
/// (phase 4c). BYOK circuits are never gated, so this never fires for a visitor on their own key.
/// </summary>
public sealed class AiCreditsExhaustedException(string? message = null)
    : Exception(message ?? AiErrorText.OutOfCredits);
