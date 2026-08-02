namespace ShelfAware.Web.Services;

/// <summary>
/// Per-circuit bus for starting the guided walkthrough, in the same shape and for the same reason as
/// <see cref="VoiceCoordinator"/>: the tour lives in <c>MainLayout</c> (so it survives the navigation it
/// performs), while the things that OFFER it — the dashboard's first-run banner, the Settings replay
/// button — are pages underneath it. Neither side needs a reference to the other.
///
/// Scoped = one instance per circuit, which is the sharing scope we want: a walkthrough is one visitor's.
/// </summary>
public sealed class TourCoordinator
{
    /// <summary>Raised when something asks for the walkthrough to run from the beginning.</summary>
    public event Func<Task>? StartRequested;

    /// <summary>Starts the walkthrough at step one, wherever the request came from.</summary>
    public Task RequestStartAsync() => InvokeAllAsync(StartRequested);

    // Invoke every subscriber in turn (a plain Func<Task>.Invoke would only await the last one).
    private static async Task InvokeAllAsync(Func<Task>? handlers)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
            await handler();
    }
}
