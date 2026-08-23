using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ShelfAware.Web.Auth;

/// <summary>Feeds <see cref="OnlinePresence"/> from this circuit's connection lifecycle. Scoped (Blazor
/// creates one per circuit and discovers every registered CircuitHandler), so it holds the circuit's own
/// captured user and reports that user's connect/disconnect to the shared singleton.
///
/// The user is captured lazily on the FIRST connection-up rather than at circuit-open: by then the
/// circuit is fully established and its <see cref="AuthenticationStateProvider"/> is reliably populated
/// from the initial request (the same provider AdminReportReader reads). Presence is keyed on the SignalR
/// connection so a closed tab drops promptly; the circuit-closed hook is a backstop for a drop the
/// down-event missed.
///
/// ⚠️ These hooks run on EVERY user's circuit, so nothing here may fault it — presence is a secondary
/// admin view and must never break the session it rides on. The two things that can throw are the auth
/// state read and a misbehaving <see cref="OnlinePresence.Changed"/> subscriber; both are contained here
/// (fail to "not shown online", log, let cancellation propagate), the same guarantee Admin.OnPresenceChanged
/// gives from the subscriber side. The guardable work lives in <see cref="HandleConnectionUpAsync"/> /
/// <see cref="SafeDisconnect"/> — a <see cref="Circuit"/> can't be constructed outside the framework, so
/// keying them on the circuit id is what lets a test drive (and mutation-check) the guard.</summary>
public sealed class PresenceCircuitHandler(
    AuthenticationStateProvider auth,
    OnlinePresence presence,
    ILogger<PresenceCircuitHandler> logger) : CircuitHandler
{
    private OnlineUser? _user;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
        => HandleConnectionUpAsync(circuit.Id);

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        SafeDisconnect(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Backstop: if the down-event was missed (an abrupt teardown), closing the circuit still clears it.
        SafeDisconnect(circuit.Id);
        return Task.CompletedTask;
    }

    /// <summary>Capture this circuit's user (once) and mark it online. Guarded: reading the auth state can
    /// throw, and <see cref="OnlinePresence.Connect"/> fans out to arbitrary Changed subscribers on THIS
    /// circuit's thread — neither may fault it, so on failure the circuit simply doesn't appear online.
    /// Cancellation propagates (the circuit is being torn down anyway).</summary>
    internal async Task HandleConnectionUpAsync(string circuitId)
    {
        try
        {
            _user ??= TryCapture((await auth.GetAuthenticationStateAsync()).User);
            if (_user is not null)
                presence.Connect(circuitId, _user, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Presence capture failed on connection-up; this circuit won't show as online.");
        }
    }

    /// <summary>Mark this circuit offline. Guarded for the same reason as connect — Disconnect fans out to
    /// Changed subscribers on this circuit's thread. No cancellation to honor here (synchronous, no token),
    /// so a stray exception is contained outright: the worst case is a circuit lingering briefly online.</summary>
    internal void SafeDisconnect(string circuitId)
    {
        try
        {
            presence.Disconnect(circuitId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Presence disconnect failed; this circuit may briefly linger in \"who's online\".");
        }
    }

    /// <summary>The account behind a principal, or null when it isn't a signed-in user (there is no
    /// circuit for an anonymous visitor in this app — every page requires auth and the account pages are
    /// static SSR — but presence must never invent an entry for one). Name is the email in this app;
    /// NameIdentifier is the stable id, falling back to the email if it's somehow absent.</summary>
    public static OnlineUser? TryCapture(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } email }) return null;
        return new OnlineUser(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? email, email);
    }
}
