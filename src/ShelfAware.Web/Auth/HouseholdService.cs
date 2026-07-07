using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace ShelfAware.Web.Auth;

/// <summary>Household operations (create / join-by-invite / rename / regenerate code / members).
/// Takes the SCOPED <see cref="AuthDbContext"/> — the same instance Identity's user store uses in a
/// request — so registration can wrap "create user + create/join household" in one transaction.</summary>
public sealed class HouseholdService(AuthDbContext db, ILogger<HouseholdService> logger)
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

    public Task<Household?> FindByInviteCodeAsync(string inviteCode, CancellationToken ct = default)
    {
        var normalized = Normalize(inviteCode);
        return db.Households.SingleOrDefaultAsync(h => h.InviteCode == normalized, ct);
    }

    /// <summary>Creates a household and makes <paramref name="user"/> its first member. The caller
    /// owns the transaction (registration wraps user-create + this in one).</summary>
    public async Task<Household> CreateForAsync(string name, AppUser user, CancellationToken ct = default)
    {
        var household = new Household { Name = name.Trim(), InviteCode = await UnusedInviteCodeAsync(ct) };
        db.Households.Add(household);
        user.HouseholdId = household.Id;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Household {HouseholdId} created.", household.Id);
        return household;
    }

    /// <summary>Joins <paramref name="user"/> to the household owning <paramref name="inviteCode"/>.
    /// Returns null when the code matches nothing (the caller shows a friendly error and rolls back).</summary>
    public async Task<Household?> JoinAsync(string inviteCode, AppUser user, CancellationToken ct = default)
    {
        var household = await FindByInviteCodeAsync(inviteCode, ct);
        if (household is null) return null;
        user.HouseholdId = household.Id;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("A member joined household {HouseholdId}.", household.Id);
        return household;
    }

    /// <summary>Replaces the invite code, cutting off anyone still holding the old one.</summary>
    public async Task<string> RegenerateInviteCodeAsync(string householdId, CancellationToken ct = default)
    {
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        household.InviteCode = await UnusedInviteCodeAsync(ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Household {HouseholdId} regenerated its invite code.", householdId);
        return household.InviteCode;
    }

    public async Task RenameAsync(string householdId, string name, CancellationToken ct = default)
    {
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        household.Name = name.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetMemberEmailsAsync(string householdId, CancellationToken ct = default)
        => await db.Users.Where(u => u.HouseholdId == householdId)
            .Select(u => u.Email ?? u.UserName ?? "(unknown)")
            .OrderBy(e => e)
            .ToListAsync(ct);

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
