namespace ShelfAware.Web.Auth;

/// <summary>The tenancy unit: a group of accounts sharing one pantry. Pantry rows carry
/// <see cref="Id"/> as a plain value (the pantry DB has no FK into this auth-side table).</summary>
public class Household
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";

    /// <summary>Uppercase share code another person enters at registration to join this household, or
    /// null when there is no code — which is the default and the resting state.
    ///
    /// A code is a bearer credential for an entire pantry, so it's a deliberate, transient act rather than
    /// a standing fixture: a household is created without one, one is minted only when a member asks for
    /// it, and it disappears again the moment its last use is redeemed or a member clears it. Possession
    /// of the code IS the authorization to join, so it's generated from a CSPRNG.
    ///
    /// NULL, not "": the unique index on this column admits any number of NULLs but only a single "", so
    /// a second code-less household would collide with the first the moment it was saved.</summary>
    public string? InviteCode { get; set; }

    /// <summary>When the code stops working, or null for never.
    ///
    /// A code is a bearer credential for someone's entire pantry, and it used to be a permanent one:
    /// anyone who ever saw it — a screenshot, a forwarded message, a shoulder — could still be creating
    /// accounts into the household a year later, on a deployment that had otherwise closed registration.
    /// Regenerating was the only revocation, and it required knowing you had a reason to.</summary>
    public DateTimeOffset? InviteExpiresAt { get; set; }

    /// <summary>How many times the code may be redeemed, or null for unlimited. The point of a limit is
    /// that inviting one person shouldn't hand out a key that admits a crowd.</summary>
    public int? InviteMaxUses { get; set; }

    /// <summary>How many times the current code has been redeemed. Reset when the code is regenerated.</summary>
    public int InviteUseCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Whether the code would be accepted right now. A method rather than a property so EF leaves
    /// it alone (it's behaviour, not a column) and so the caller has to name the clock — which is what
    /// makes expiry testable without waiting.</summary>
    public bool InviteIsUsable(DateTimeOffset now) =>
        !string.IsNullOrEmpty(InviteCode)
        && (InviteExpiresAt is null || InviteExpiresAt > now)
        && (InviteMaxUses is null || InviteUseCount < InviteMaxUses);

    /// <summary>Uses left on the code, or null when it's unlimited — for telling the user what they're
    /// about to share.</summary>
    public int? InviteUsesRemaining => InviteMaxUses is null ? null : Math.Max(0, InviteMaxUses.Value - InviteUseCount);
}
