namespace ShelfAware.Web.Services;

/// <summary>
/// Per-circuit bus between the layout-hosted <c>VoiceAgent</c> and the pages. The agent lives in
/// <c>MainLayout</c> (so it survives navigation and keeps listening); the pages come and go under it.
/// Two decoupled signals ride this coordinator so neither side needs a direct reference to the other:
/// <list type="bullet">
///   <item><see cref="PantryChanged"/> — the agent applied a data change by voice; the page currently
///   on screen reloads so its lists reflect it (replacing the old per-page <c>OnApplied</c> callback,
///   which no longer works now that the agent isn't a child of the dashboard).</item>
///   <item><see cref="ResumeRequested"/> — a read-aloud/cook-along surface hands control back to the
///   agent ("Back to assistant"), so it re-opens the mic and resumes the conversation.</item>
/// </list>
/// Scoped = one instance per Blazor circuit (single user session), which is exactly the sharing scope
/// we want. Handlers are <see cref="Func{Task}"/> so subscribers can marshal onto the renderer.
/// </summary>
public sealed class VoiceCoordinator
{
    /// <summary>What the user is currently looking at (e.g. the recipes on screen, in display order), or
    /// null. The page on screen sets it; the roaming voice agent reads it at call time and passes it to
    /// the chat so on-screen references ("read me the second one") resolve. Plain property — the agent
    /// just reads the latest value; no notification needed. Pages clear it (null) when they navigate away.</summary>
    public string? ScreenContext { get; set; }

    /// <summary>Raised after a voice command changed pantry data; the visible page reloads.</summary>
    public event Func<Task>? PantryChanged;

    /// <summary>Raised when a surface hands control back to the agent to resume listening.</summary>
    public event Func<Task>? ResumeRequested;

    public Task NotifyPantryChangedAsync() => InvokeAllAsync(PantryChanged);

    public Task RequestResumeAsync() => InvokeAllAsync(ResumeRequested);

    // Invoke every subscriber in turn (a plain Func<Task>.Invoke would only await the last one).
    private static async Task InvokeAllAsync(Func<Task>? handlers)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
            await handler();
    }
}
