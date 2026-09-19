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
    /// <param name="screenContext">A short description of what the user is currently looking at (e.g. the
    /// recipes listed on screen, in display order), or null. Lets the model resolve on-screen references
    /// — "read me the second one", "that one" — to a concrete name it can act on. The roaming voice agent
    /// fills this from the page it's currently on.</param>
    /// <param name="cookAlong">The hands-free recipe reader currently on screen, or null when there is
    /// none. Structured rather than inferred from <paramref name="screenContext"/>, which is prose written
    /// for the model to read: whether a step can be moved to is a question the code has to answer, and
    /// answering it by looking for words in a sentence meant for a model is how the two drift apart.</param>
    Task<ChatResult> HandleAsync(
        string userText, IReadOnlyList<ChatTurn>? history = null, string? screenContext = null,
        CookAlongState? cookAlong = null, CancellationToken cancellationToken = default);
}

/// <summary>The hands-free recipe reader that is open right now.
///
/// <para>⚠️ It exists so the chat cannot ANNOUNCE a move it has no way to make. <c>go_to_step</c> was
/// offered to the model on every surface and range-checked on none: the handler recorded the step and
/// replied "Moving to step 12", the model repeated it, and the only consumer — the reader — silently
/// dropped anything past the end of the recipe or, on the dashboard and the push-to-talk button, did not
/// exist at all. So the household was told the screen had moved while it sat still, and was charged for
/// it. The tool is now offered only when this is present, and the step is checked against
/// <see cref="StepCount"/> before anyone is told anything.</para></summary>
/// <param name="StepCount">How many steps the open recipe has. Step 0 is the introduction, so the legal
/// range is 0..StepCount inclusive.</param>
public record CookAlongState(int StepCount);

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

    /// <summary>True when this navigation hands control off to a surface that will produce its own audio
    /// (currently read_recipe → the recipe read-aloud). A persistent listening agent should STOP
    /// listening on a hand-off so it doesn't hear the reading; ordinary navigation (open_page) leaves it
    /// listening so commands can be chained. Ignored by single-shot surfaces (push-to-talk).</summary>
    public bool HandsOff { get; init; }

    /// <summary>
    /// The step a hands-free reader should jump to (1-based; 0 = the recipe's introduction), or null.
    /// Set by the go_to_step tool and meaningful only to a cook-along that's on screen — everything else
    /// ignores it.
    ///
    /// This is what stops the cook-along's plain-code grammar from having to be exhaustive. That grammar
    /// makes "next" instant and free, but it matches whole utterances, so anything it doesn't recognise —
    /// a cough, a stutter, "up next", a phrasing nobody thought of — used to be ANSWERED instead of
    /// obeyed, which is the wrong outcome rather than a slow one. With this, a miss just costs a model
    /// call: the grammar is an optimisation, not a gate.
    /// </summary>
    public int? StepTarget { get; init; }

    public static ChatResult Ok(
        string reply, IReadOnlyList<string> actions, string? navigateTo = null, bool handsOff = false,
        int? stepTarget = null) =>
        new()
        {
            Success = true, Reply = reply, Actions = actions,
            NavigateTo = navigateTo, HandsOff = handsOff, StepTarget = stepTarget,
        };

    public static ChatResult Fail(string reply) =>
        new() { Success = false, Reply = reply };
}
