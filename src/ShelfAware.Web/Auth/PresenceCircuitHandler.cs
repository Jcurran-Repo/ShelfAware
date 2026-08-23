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
/// down-event missed.</summary>
public sealed class PresenceCircuitHandler(AuthenticationStateProvider auth, OnlinePresence presence) : CircuitHandler
{
    private OnlineUser? _user;

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _user ??= TryCapture((await auth.GetAuthenticationStateAsync()).User);
        if (_user is not null)
            presence.Connect(circuit.Id, _user, DateTimeOffset.Now);
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        presence.Disconnect(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Backstop: if the down-event was missed (an abrupt teardown), closing the circuit still clears it.
        presence.Disconnect(circuit.Id);
        return Task.CompletedTask;
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
