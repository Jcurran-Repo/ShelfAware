namespace ShelfAware.Core.Chat;

/// <summary>
/// Single-turn natural-language quick-update for the dashboard (DESIGN.md §7).
/// Implementations run a tool-calling loop ("we're out of dog food, low on coffee"
/// → record_signal × 2) and reply with a one-line confirmation. Behind an interface
/// so the provider is swappable and the dashboard is testable without API calls.
/// </summary>
public interface IPantryChat
{
    Task<ChatResult> HandleAsync(string userText, CancellationToken cancellationToken = default);
}

public record ChatResult
{
    public bool Success { get; init; }

    /// <summary>One-line confirmation shown to the user.</summary>
    public required string Reply { get; init; }

    /// <summary>Short descriptions of state changes applied, for UI transparency.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    public static ChatResult Ok(string reply, IReadOnlyList<string> actions) =>
        new() { Success = true, Reply = reply, Actions = actions };

    public static ChatResult Fail(string reply) =>
        new() { Success = false, Reply = reply };
}
