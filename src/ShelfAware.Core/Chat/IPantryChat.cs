namespace ShelfAware.Core.Chat;

/// <summary>
/// Single-turn natural-language quick-update for the dashboard (DESIGN.md §7).
/// Implementations run a tool-calling loop ("we're out of dog food, low on coffee"
/// → record_signal × 2) and reply with a one-line confirmation. Behind an interface
/// so the provider is swappable and the dashboard is testable without API calls.
/// </summary>
public interface IPantryChat
{
    /// <param name="history">Prior turns of the same conversation, oldest first, so a follow-up can
    /// reference earlier context ("add the first two"). Null/empty = a fresh single-turn request
    /// (the original dashboard behaviour).</param>
    Task<ChatResult> HandleAsync(
        string userText, IReadOnlyList<ChatTurn>? history = null, CancellationToken cancellationToken = default);
}

/// <summary>One completed exchange in a multi-turn voice conversation (v2.1). The assistant's reply
/// text carries enough context that replaying (user, assistant) pairs lets the model resolve
/// references on the next turn — so we don't need to persist the intermediate tool calls.</summary>
public record ChatTurn(string User, string Assistant);

public record ChatResult
{
    public bool Success { get; init; }

    /// <summary>One-line confirmation shown to the user.</summary>
    public required string Reply { get; init; }

    /// <summary>Short descriptions of state changes applied, for UI transparency.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>Relative URL the UI should navigate to after showing/speaking the reply (set by the
    /// open_page / read_recipe tools, e.g. "/recipes?read=12"), or null. A plain string so Core stays
    /// UI-framework-agnostic; the Blazor layer applies it with its NavigationManager.</summary>
    public string? NavigateTo { get; init; }

    public static ChatResult Ok(string reply, IReadOnlyList<string> actions, string? navigateTo = null) =>
        new() { Success = true, Reply = reply, Actions = actions, NavigateTo = navigateTo };

    public static ChatResult Fail(string reply) =>
        new() { Success = false, Reply = reply };
}
