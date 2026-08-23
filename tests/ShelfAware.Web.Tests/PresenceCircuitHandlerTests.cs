using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>The circuit-handler side of presence — the wiring bUnit can't drive (a <c>Circuit</c> can't be
/// constructed, so the framework hooks delegate to id-keyed internal methods these tests call directly).
/// The load-bearing claims: a signed-in circuit is captured and marked online; the user is captured ONCE
/// and reused across a reconnect; and — the whole point of the guards — neither a throwing auth state nor
/// a throwing Changed subscriber can fault the innocent user's circuit these hooks run on.</summary>
public class PresenceCircuitHandlerTests
{
    private static ClaimsPrincipal SignedIn(string id, string email) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, email), new Claim(ClaimTypes.NameIdentifier, id)],
            authenticationType: "test"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static PresenceCircuitHandler Handler(AuthenticationStateProvider auth, OnlinePresence presence) =>
        new(auth, presence, NullLogger<PresenceCircuitHandler>.Instance);

    /// <summary>A provider whose principal can change between calls (a reconnect that revalidated to
    /// anonymous) and that can be told to throw (the auth state not being ready at connection-up).</summary>
    private sealed class FakeAuthState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        private ClaimsPrincipal _principal = principal;
        public bool Throw { get; set; }
        public int Calls { get; private set; }
        public void SetPrincipal(ClaimsPrincipal principal) => _principal = principal;

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("GetAuthenticationStateAsync was called before SetAuthenticationState.");
            return Task.FromResult(new AuthenticationState(_principal));
        }
    }

    [Fact]
    public async Task A_signed_in_circuit_is_captured_and_marked_online()
    {
        var presence = new OnlinePresence();
        var handler = Handler(new FakeAuthState(SignedIn("u1", "a@example.com")), presence);

        await handler.HandleConnectionUpAsync("circuit-1");

        var entry = Assert.Single(presence.Snapshot());
        Assert.Equal("u1", entry.User.UserId);
        Assert.Equal("a@example.com", entry.User.Email);
    }

    [Fact]
    public async Task A_throwing_auth_state_is_contained_and_adds_nobody()
    {
        var presence = new OnlinePresence();
        var handler = Handler(new FakeAuthState(Anonymous()) { Throw = true }, presence);

        // No exception escapes onto the circuit (dropping the guard makes this line throw), and a failed
        // capture adds nobody rather than inventing a phantom "online" entry.
        await handler.HandleConnectionUpAsync("circuit-1");

        Assert.Empty(presence.Snapshot());
    }

    [Fact]
    public async Task The_captured_user_survives_a_reconnect_whose_auth_state_went_anonymous()
    {
        var presence = new OnlinePresence();
        var auth = new FakeAuthState(SignedIn("u1", "a@example.com"));
        var handler = Handler(auth, presence);

        await handler.HandleConnectionUpAsync("circuit-1"); // captures u1
        handler.SafeDisconnect("circuit-1");                // a transient drop clears the entry
        Assert.Empty(presence.Snapshot());

        // The same circuit reconnects, but its auth state now reads anonymous (mid-revalidation). The user
        // was captured once and is REUSED — the account must not silently drop off "who's online".
        auth.SetPrincipal(Anonymous());
        await handler.HandleConnectionUpAsync("circuit-1");

        Assert.Single(presence.Snapshot());  // still u1 — _user ??= reuses; mutating it to _user = empties this
        Assert.Equal(1, auth.Calls);         // and the reconnect never re-read the auth state
    }

    [Fact]
    public async Task A_throwing_Changed_subscriber_cannot_fault_a_connect()
    {
        var presence = new OnlinePresence();
        presence.Changed += () => throw new InvalidOperationException("a buggy re-render on some viewer's page");
        var handler = Handler(new FakeAuthState(SignedIn("u1", "a@example.com")), presence);

        // Connect fans Changed out to the throwing subscriber on THIS circuit's thread; the handler
        // contains it (dropping the guard makes this throw), and the entry is still recorded.
        await handler.HandleConnectionUpAsync("circuit-1");

        Assert.Single(presence.Snapshot());
    }

    [Fact]
    public async Task A_throwing_Changed_subscriber_cannot_fault_a_disconnect()
    {
        var presence = new OnlinePresence();
        var handler = Handler(new FakeAuthState(SignedIn("u1", "a@example.com")), presence);
        await handler.HandleConnectionUpAsync("circuit-1"); // subscribe AFTER, so the connect itself is clean

        presence.Changed += () => throw new InvalidOperationException("a buggy re-render on some viewer's page");

        // Disconnect fans Changed out to the throwing subscriber; SafeDisconnect contains it (dropping
        // that guard makes this throw) and the entry is still removed.
        handler.SafeDisconnect("circuit-1");

        Assert.Empty(presence.Snapshot());
    }
}
