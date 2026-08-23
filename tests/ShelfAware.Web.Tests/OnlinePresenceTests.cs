using System.Security.Claims;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>The live who's-online tracker. The load-bearing claims: it counts distinct ACCOUNTS not
/// connections (several tabs are one person), "since" is the earliest connection, disconnect removes,
/// and the Changed event fires exactly when the online set actually moves.</summary>
public class OnlinePresenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 9, 0, 0, TimeSpan.FromHours(-5));
    private static OnlineUser User(string id, string email) => new(id, email);

    [Fact]
    public void A_connection_makes_the_account_online()
    {
        var presence = new OnlinePresence();
        presence.Connect("c1", User("u1", "a@example.com"), T0);

        Assert.Equal(1, presence.OnlineCount);
        var entry = Assert.Single(presence.Snapshot());
        Assert.Equal("a@example.com", entry.User.Email);
        Assert.Equal(1, entry.Connections);
        Assert.Equal(T0, entry.Since);
    }

    [Fact]
    public void Several_tabs_from_one_account_are_one_person_with_a_connection_count()
    {
        var presence = new OnlinePresence();
        presence.Connect("c1", User("u1", "a@example.com"), T0);
        presence.Connect("c2", User("u1", "a@example.com"), T0.AddMinutes(5));

        Assert.Equal(1, presence.OnlineCount); // ONE account, not two connections
        var entry = Assert.Single(presence.Snapshot());
        Assert.Equal(2, entry.Connections);
        Assert.Equal(T0, entry.Since); // the EARLIEST connection
    }

    [Fact]
    public void Different_accounts_are_counted_separately()
    {
        var presence = new OnlinePresence();
        presence.Connect("c1", User("u1", "a@example.com"), T0);
        presence.Connect("c2", User("u2", "b@example.com"), T0);

        Assert.Equal(2, presence.OnlineCount);
        Assert.Equal(["a@example.com", "b@example.com"], presence.Snapshot().Select(e => e.User.Email));
    }

    [Fact]
    public void Disconnecting_the_last_connection_takes_the_account_offline()
    {
        var presence = new OnlinePresence();
        presence.Connect("c1", User("u1", "a@example.com"), T0);
        presence.Connect("c2", User("u1", "a@example.com"), T0);

        presence.Disconnect("c1");
        Assert.Equal(1, presence.OnlineCount); // still online via the other tab
        Assert.Equal(1, presence.Snapshot()[0].Connections);

        presence.Disconnect("c2");
        Assert.Equal(0, presence.OnlineCount);
        Assert.Empty(presence.Snapshot());
    }

    [Fact]
    public void Changed_fires_on_a_real_connect_and_disconnect_but_not_on_no_ops()
    {
        var presence = new OnlinePresence();
        var fired = 0;
        presence.Changed += () => fired++;

        presence.Connect("c1", User("u1", "a@example.com"), T0);
        Assert.Equal(1, fired);

        // A reconnect of the SAME circuit id is idempotent — the set didn't move, so no event.
        presence.Connect("c1", User("u1", "a@example.com"), T0.AddMinutes(1));
        Assert.Equal(1, fired);

        // Disconnecting an unknown circuit changes nothing.
        presence.Disconnect("never-connected");
        Assert.Equal(1, fired);

        presence.Disconnect("c1");
        Assert.Equal(2, fired);
    }

    [Fact]
    public void TryCapture_reads_a_signed_in_account_and_refuses_an_anonymous_one()
    {
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "a@example.com"), new Claim(ClaimTypes.NameIdentifier, "u1")],
            authenticationType: "test"));
        var captured = PresenceCircuitHandler.TryCapture(signedIn);
        Assert.NotNull(captured);
        Assert.Equal("u1", captured!.UserId);
        Assert.Equal("a@example.com", captured.Email);

        // Anonymous (no authentication type) → nothing to track.
        Assert.Null(PresenceCircuitHandler.TryCapture(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void TryCapture_falls_back_to_the_email_when_the_id_claim_is_absent()
    {
        var noId = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "a@example.com")], authenticationType: "test"));

        var captured = PresenceCircuitHandler.TryCapture(noId);
        Assert.Equal("a@example.com", captured!.UserId); // email stands in as the key
        Assert.Equal("a@example.com", captured.Email);
    }
}
