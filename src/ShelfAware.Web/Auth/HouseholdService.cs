using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ShelfAware.Web.Auth;

/// <summary>One account in a household. The id is what a "Remove" button needs; the email is what a
/// human recognises.</summary>
public sealed record HouseholdMember(string Id, string Email);

/// <summary>Household operations (create / join-by-invite / rename / regenerate code / members).
/// Takes the SCOPED <see cref="AuthDbContext"/> — the same instance Identity's user store uses in a
/// request — so registration can wrap "create user + create/join household" in one transaction.</summary>
public sealed class HouseholdService(
    AuthDbContext db, UserManager<AppUser> users, IOptions<AuthOptions> options, ILogger<HouseholdService> logger)
{
    /// <summary>Unambiguous alphabet (no 0/O, 1/I/L) so a code survives being read aloud or
    /// handwritten. ~31^10 ≈ 8×10^14 combinations at length 10.</summary>
    private const string InviteAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int InviteCodeLength = 10;

    public static string NewInviteCode() => RandomNumberGenerator.GetString(InviteAlphabet, InviteCodeLength);

    /// <summary>The registration gate for CREATING a new household by self-signup. Joining an existing
    /// household with a valid invite code is always allowed (the code is the authorization), and the
    /// very first user is always allowed so a locked-down fresh deploy is enterable.</summary>
    public static bool MayCreateHousehold(bool allowRegistration, bool anyUsersExist)
        => allowRegistration || !anyUsersExist;

    public Task<bool> AnyUsersAsync(CancellationToken ct = default) => db.Users.AnyAsync(ct);

    public Task<Household?> GetAsync(string householdId, CancellationToken ct = default)
        => db.Households.SingleOrDefaultAsync(h => h.Id == householdId, ct);

    /// <summary>The household a code admits you to, or null if the code is unknown, expired, or used up.
    /// The three are deliberately indistinguishable to the caller: telling someone their guess was "a real
    /// code, but expired" confirms the guess.</summary>
    public async Task<Household?> FindByInviteCodeAsync(string inviteCode, CancellationToken ct = default)
    {
        var normalized = Normalize(inviteCode);
        if (normalized.Length == 0) return null; // never let "" match a household with no code set

        var household = await db.Households.SingleOrDefaultAsync(h => h.InviteCode == normalized, ct);
        return household?.InviteIsUsable(DateTimeOffset.Now) == true ? household : null;
    }

    /// <summary>Creates a household and makes <paramref name="user"/> its first member. The caller
    /// owns the transaction (registration wraps user-create + this in one).</summary>
    public async Task<Household> CreateForAsync(string name, AppUser user, CancellationToken ct = default)
    {
        var household = new Household { Name = name.Trim() };
        await ResetInviteAsync(household, maxUses: null, ct);
        db.Households.Add(household);
        user.HouseholdId = household.Id;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Household {HouseholdId} created.", household.Id);
        return household;
    }

    /// <summary>Joins <paramref name="user"/> to the household owning <paramref name="inviteCode"/>.
    /// Returns null when the code matches nothing, has expired, or has no uses left (the caller shows a
    /// friendly error and rolls back).</summary>
    public async Task<Household?> JoinAsync(string inviteCode, AppUser user, CancellationToken ct = default)
    {
        var household = await FindByInviteCodeAsync(inviteCode, ct);
        if (household is null) return null;

        // Claim a use with a CONDITIONAL update rather than incrementing what we read: the check above and
        // the write below are otherwise a race, and two people redeeming a single-use code at the same
        // moment would both pass it. One statement, so the database decides who got the last use. It runs
        // on this context, so it's inside the caller's registration transaction and rolls back with it.
        var claimed = await db.Households
            .Where(h => h.Id == household.Id && (h.InviteMaxUses == null || h.InviteUseCount < h.InviteMaxUses))
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.InviteUseCount, h => h.InviteUseCount + 1), ct);
        if (claimed == 0)
        {
            logger.LogInformation("An invite for household {HouseholdId} was redeemed after its last use went.", household.Id);
            return null;
        }

        // ExecuteUpdate wrote straight to the database and the change tracker never saw it, so the entity
        // we're about to hand back still reports the pre-join use count. Re-read it: a caller asking the
        // returned household how many uses are left deserves the answer, not the one from a moment ago.
        await db.Entry(household).ReloadAsync(ct);

        user.HouseholdId = household.Id;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("A member joined household {HouseholdId}.", household.Id);
        return household;
    }

    /// <summary>Replaces the invite code, cutting off anyone still holding the old one. Resets the expiry
    /// and the use count too: a regenerated code is a new credential, not the old one with a new spelling.</summary>
    public async Task<Household> RegenerateInviteCodeAsync(
        string householdId, int? maxUses = null, CancellationToken ct = default)
    {
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        await ResetInviteAsync(household, maxUses, ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Household {HouseholdId} regenerated its invite code (max uses: {MaxUses}).",
            householdId, maxUses?.ToString() ?? "unlimited");
        return household;
    }

    private async Task ResetInviteAsync(Household household, int? maxUses, CancellationToken ct)
    {
        household.InviteCode = await UnusedInviteCodeAsync(ct);
        household.InviteUseCount = 0;
        household.InviteMaxUses = maxUses;
        // Only ABSENT means "never expires". A configured 0 or negative used to mean it too, which made a
        // typo silently switch the expiry off — the least safe reading of a mistake. Startup validation
        // (Program.cs) now refuses anything below 1, so a value here is a real lifetime; and if one ever
        // slipped through, AddDays(0) expires the code immediately, which fails closed.
        household.InviteExpiresAt = options.Value.InviteCodeLifetimeDays is { } days
            ? DateTimeOffset.Now.AddDays(days)
            : null;
    }

    public async Task RenameAsync(string householdId, string name, CancellationToken ct = default)
    {
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        household.Name = name.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HouseholdMember>> GetMembersAsync(string householdId, CancellationToken ct = default)
        // Ordered before the projection: SQLite can sort a column, not a record it's never heard of.
        => await db.Users.Where(u => u.HouseholdId == householdId)
            .OrderBy(u => u.Email ?? u.UserName)
            .Select(u => new HouseholdMember(u.Id, u.Email ?? u.UserName ?? "(unknown)"))
            .ToListAsync(ct);

    /// <summary>
    /// Removes a member from a household. There was previously no way to do this at all: joining was
    /// permanent, so a shared invite or a compromised account could only be dealt with by regenerating the
    /// code, which stops new joins and evicts precisely nobody.
    ///
    /// The pantry is untouched — it belongs to the household, not the account (the same principle as
    /// "delete my account"). The member simply stops being able to reach it, and lands on the household
    /// chooser next time they sign in.
    ///
    /// Bumping the security stamp is the part that actually does the removing. The household id is baked
    /// into the auth COOKIE at sign-in, so clearing the column alone would leave their existing cookie
    /// asserting membership until it happened to be re-issued — they'd keep reading the pantry for days.
    /// The stamp change invalidates the cookie and kills their live circuits at the next revalidation
    /// (5 minutes), which is also the bound on how long a removal takes to bite.
    ///
    /// Both parties are checked HERE rather than trusted from the caller. Settings.razor does derive both
    /// ids from the caller's own claim, so nothing today can misuse this — but this method is the
    /// authorization boundary for evicting someone from their data, and a boundary that relies on its one
    /// caller having been careful isn't one.
    /// </summary>
    /// <returns>Null on success, or why it was refused.</returns>
    public async Task<string?> RemoveMemberAsync(
        string householdId, string userId, string actingUserId, CancellationToken ct = default)
    {
        if (userId == actingUserId) return "You can't remove yourself. Use 'Delete my account' instead.";

        // You may only remove people from a household you are in yourself. Anyone in it may remove anyone
        // else — the app has no roles and says so ("everyone in your household shares this pantry") — but
        // "anyone" means a member, not anyone at all.
        var actor = await db.Users.SingleOrDefaultAsync(u => u.Id == actingUserId, ct);
        if (actor is null || actor.HouseholdId != householdId)
            return "You can only remove people from your own household.";

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.HouseholdId != householdId)
        {
            // Includes the case where they were removed a moment ago by someone else.
            return "That person isn't in this household.";
        }

        // Note there's no separate "can't remove the last member" rule, because there can't be a last
        // member to remove: the actor and the target are both in this household and are not the same
        // person, so it has at least two, and the actor is still in it afterwards. The invariant that a
        // household never empties out — its pantry would be data nobody could read, export, or delete —
        // falls out of the two checks above rather than needing a third that could never fire.
        user.HouseholdId = null;
        await db.SaveChangesAsync(ct);

        var stamped = await users.UpdateSecurityStampAsync(user);
        if (!stamped.Succeeded)
        {
            // The column is already cleared, so they'll be out as soon as their cookie is next re-issued —
            // but that could be days, and the caller deserves to know it isn't immediate.
            logger.LogError(
                "Removed {UserId} from household {HouseholdId} but couldn't bump their security stamp: {Errors}",
                userId, householdId, string.Join("; ", stamped.Errors.Select(e => e.Description)));
            return "Removed — but their existing sign-in may take a while to expire. Change the invite code too.";
        }

        logger.LogInformation("A member was removed from household {HouseholdId}.", householdId);
        return null;
    }

    private static string Normalize(string inviteCode) => inviteCode.Trim().ToUpperInvariant();

    private async Task<string> UnusedInviteCodeAsync(CancellationToken ct)
    {
        // Collisions are ~impossible at this key space, but the unique index makes one a hard failure,
        // so check-and-retry a few times rather than surfacing a constraint violation to a registrant.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = NewInviteCode();
            if (!await db.Households.AnyAsync(h => h.InviteCode == code, ct)) return code;
        }
        throw new InvalidOperationException("Could not generate an unused invite code.");
    }
}
