using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;
using ShelfAware.Llm;

namespace ShelfAware.Web.Auth;

/// <summary>One account in a household. The id is what a "Remove" button needs; the email is what a
/// human recognises.</summary>
public sealed record HouseholdMember(string Id, string Email);

/// <summary>Household operations (create / join-by-invite / rename / regenerate code / members).
/// Takes the SCOPED <see cref="AuthDbContext"/> — the same instance Identity's user store uses in a
/// request — so registration can wrap "create user + create/join household" in one transaction.</summary>
public sealed class HouseholdService(
    AuthDbContext db, UserManager<AppUser> users, IOptions<AuthOptions> options,
    IOptions<LlmOptions> llm, IOptions<BillingOptions> billing, ILogger<HouseholdService> logger)
{
    /// <summary>Unambiguous alphabet (no 0/O, 1/I/L) so a code survives being read aloud or
    /// handwritten. ~31^10 ≈ 8×10^14 combinations at length 10.</summary>
    private const string InviteAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int InviteCodeLength = 10;

    public static string NewInviteCode() => RandomNumberGenerator.GetString(InviteAlphabet, InviteCodeLength);

    /// <summary>The registration gate for CREATING a new household by self-signup. Joining an existing
    /// household with a valid invite code is always allowed (the code is the authorization), and the very
    /// first household is always allowed so a locked-down fresh deploy is enterable.
    ///
    /// The bootstrap signal is "no household exists yet", NOT "no user exists". On a confirmation-required
    /// box the account is created BEFORE the household (the household is chosen after the registrant proves
    /// inbox control — the pre-hijack fix), so a user can exist with none, and keying the bootstrap on users
    /// would leave the very first person unable to create the very first household. On a direct-registration
    /// box every user has a household, so the two are equivalent there.</summary>
    public static bool MayCreateHousehold(bool allowRegistration, bool anyHouseholdsExist)
        => allowRegistration || !anyHouseholdsExist;

    public Task<bool> AnyHouseholdsAsync(CancellationToken ct = default) => db.Households.AnyAsync(ct);

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
    /// owns the transaction (registration wraps user-create + this in one), and must have PERSISTED the
    /// user first — every production caller does (UserManager writes the row before we're reached, or
    /// ChooseHousehold loads an existing one), and the slot-claim below is a direct UPDATE that can't see
    /// a user still staged on the change tracker.
    ///
    /// The new household has NO invite code. Minting one here is what made a code a standing fixture —
    /// every household permanently advertising a key to its own pantry, whether or not anyone had ever
    /// wanted to invite a soul. A code now costs a deliberate click (<see cref="GenerateInviteCodeAsync"/>).</summary>
    public async Task<Household> CreateForAsync(string name, AppUser user, CancellationToken ct = default)
    {
        var household = new Household { Name = name.Trim() };

        // Claim the user's household slot with a CONDITIONAL update rather than assigning it unconditionally.
        // The razor guard that checks "you have no household yet" and this assignment are otherwise a race:
        // two concurrent posts (a double-clicked Create, two onboarding tabs) both pass the guard, both
        // create a household, and the last write wins the user's single slot — silently ORPHANING the other
        // household and its pantry (data nobody can read, export, or delete — and, on a managed box, a
        // welcome grant nobody can spend). One statement, so the database decides who created it: only the
        // FIRST update matches `HouseholdId == null`. It runs on this context, inside the caller's
        // registration transaction, and rolls back with it.
        //
        // Same shape as JoinAsync's use-claim below, and it leans on the same invariant every caller honours
        // — the user row already EXISTS — because a direct UPDATE can't touch a row that isn't in the
        // database yet. There is no FK from AppUser.HouseholdId to Household.Id (households and pantry data
        // span two DB files, so there couldn't be), which is what lets the slot be claimed before the
        // household row is inserted.
        var claimed = await db.Users
            .Where(u => u.Id == user.Id && u.HouseholdId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.HouseholdId, household.Id), ct);
        if (claimed == 0)
        {
            // Lost the race, or the user already had a household. Create NOTHING — hand back the one they
            // actually belong to now. Re-read because the direct update bypassed the change tracker, so the
            // tracked entity's slot is stale (and a lost race means someone else set it).
            await db.Entry(user).ReloadAsync(ct);
            var existingId = user.HouseholdId ?? throw new InvalidOperationException(
                $"CreateForAsync claimed no slot for user {user.Id} and it has no household — the user row "
                + "must be persisted before this call.");
            // Parity with JoinAsync's lost-claim log: a genuinely-concurrent double-create is invisible
            // otherwise, and this is the one line that confirms the guard fired in production.
            logger.LogInformation(
                "A create for user {UserId} found the household slot already taken (a concurrent winner, or "
                + "an already-placed user); returned existing household {HouseholdId}, created nothing.",
                user.Id, existingId);
            return await db.Households.SingleAsync(h => h.Id == existingId, ct);
        }

        db.Households.Add(household);

        // The one-time welcome grant, added to THIS transaction so it's atomic with creation — a
        // household never exists without it, and a rolled-back registration leaves none. This is the one
        // choke point every creation path shares (Register / ExternalLogin / ChooseHousehold / DevAuth all
        // call here), so an OAuth signup can't silently miss it. Managed-only: a BYOK/self-host box has no
        // host-credit concept, so a credit row there would be meaningless noise. The claim above guards it
        // too: the losing double-post returns before here, so a race can't seed a second grant.
        if (llm.Value.IsManaged && CreditLedger.WelcomeGrant(household.Id, billing.Value) is { } welcome)
        {
            db.CreditLedger.Add(welcome);
        }

        await db.SaveChangesAsync(ct);

        // The slot-claim wrote straight to the database, bypassing the change tracker, so the tracked entity
        // still shows the pre-claim slot. Sync it (as JoinAsync does after its own claim) so the caller's
        // cookie re-issue reads the real HouseholdId.
        await db.Entry(user).ReloadAsync(ct);
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
        //
        // Spending the last use also RETIRES the code, in this same statement. A code that has been used
        // up is already refused by InviteIsUsable, so clearing it changes no access decision — what it
        // changes is what the household is still holding: a spent code left in the column is a dead
        // credential sitting on the Settings page looking like a live one, and the next reader can't tell
        // "nobody has been invited" from "somebody already came". Both are the resting state; only one of
        // them says so. Doing it here rather than in a follow-up write is what keeps it honest under a
        // race — the row that loses the claim never reaches this code at all.
        //
        // Both assignments read the PRE-update row (SQL evaluates a SET list against the old values), so
        // InviteUseCount + 1 is the count this claim is about to produce.
        //
        // The code is part of the WHERE, not just the lookup above: this claims a use of THE CODE WE
        // MATCHED, not of whatever code the household happens to hold by the time the write lands. Without
        // it the statement says "spend a use, whatever the credential is now" — so a Clear or a Replace
        // committed in the window between the lookup and here would be honoured by the read and ignored by
        // the write, and the one-click revocation this class now advertises would have a hole in it exactly
        // the width of that window. Being inside the caller's transaction narrows the window; it doesn't
        // close it, and a claim that only holds because of the isolation level around it isn't one.
        var matchedCode = household.InviteCode;
        var claimed = await db.Households
            .Where(h => h.Id == household.Id
                && h.InviteCode == matchedCode
                && (h.InviteMaxUses == null || h.InviteUseCount < h.InviteMaxUses))
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.InviteUseCount, h => h.InviteUseCount + 1)
                .SetProperty(h => h.InviteCode, h =>
                    h.InviteMaxUses != null && h.InviteUseCount + 1 >= h.InviteMaxUses ? null : h.InviteCode), ct);
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

    /// <summary>Mints an invite code for a household, replacing any code it already had (which cuts off
    /// anyone still holding the old one). This is the ONLY way a code comes into existence — households
    /// are created without one — so it serves as both "generate" and "regenerate"; the household can't
    /// tell the difference and neither should the caller.
    ///
    /// <paramref name="maxUses"/> defaults to a single use: inviting one person shouldn't hand out a key
    /// that admits a crowd, and the caller has to say so explicitly to widen it. The expiry and the use
    /// count reset too — a new code is a new credential, not the old one with a new spelling.</summary>
    public async Task<Household> GenerateInviteCodeAsync(
        string householdId, int? maxUses = 1, CancellationToken ct = default)
    {
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
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
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Household {HouseholdId} generated an invite code (max uses: {MaxUses}).",
            householdId, maxUses?.ToString() ?? "unlimited");
        return household;
    }

    /// <summary>Revokes the household's invite code, returning it to having none. The one-click answer to
    /// "I pasted that in the wrong window" — previously the only way to kill a code was to mint a
    /// replacement, which left a live credential lying around as the price of revoking one.
    ///
    /// Clears the limits alongside the code, since they describe the code. Note this is tidiness, not a
    /// rule the type enforces: a code retired by its last use leaves its own limits behind (see
    /// <see cref="JoinAsync"/>, which nulls the code in the claim statement and nothing else). Either way
    /// nothing reads them while <see cref="Household.InviteCode"/> is null, and generating the next code
    /// overwrites all four.
    ///
    /// Removes nobody who already joined — that's <see cref="RemoveMemberAsync"/>. Idempotent. Returns the
    /// household so the caller can render the result rather than re-reading it and hoping it got the same
    /// tracked instance back.</summary>
    public async Task<Household> ClearInviteCodeAsync(string householdId, CancellationToken ct = default)
    {
        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        if (household.InviteCode is null) return household;

        household.InviteCode = null;
        household.InviteUseCount = 0;
        household.InviteMaxUses = null;
        household.InviteExpiresAt = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Household {HouseholdId} cleared its invite code.", householdId);
        return household;
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
        // You may only remove people from a household you are in yourself. Anyone in it may remove anyone
        // else — the app has no roles and says so ("everyone in your household shares this pantry") — but
        // "anyone" means a member, not anyone at all.
        var actor = await db.Users.SingleOrDefaultAsync(u => u.Id == actingUserId, ct);
        if (actor is null || actor.HouseholdId != householdId)
            return "You can only remove people from your own household.";

        if (userId == actingUserId)
        {
            // The advice must be true for whoever reads it. With housemates, any of them can do the
            // removing; alone, nothing can — no account deletion exists yet — so the nearest real act is
            // the pantry wipe. That pointer is only safe advice when there is nobody else's data behind
            // it: offered to a MULTI-member household, it would invite a leaver to destroy the shared pantry.
            var hasHousemates = await db.Users.AnyAsync(
                u => u.HouseholdId == householdId && u.Id != actingUserId, ct);
            return hasHousemates
                ? "You can't remove yourself — ask another member of your household to remove you."
                : "You can't remove yourself — you're this household's only member. To start fresh instead, use 'Delete all my data' below.";
        }

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
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        user.HouseholdId = null;
        await db.SaveChangesAsync(ct);

        // ⚠️ Revoke the removed member's API tokens in the SAME transaction. A token carries the household
        // id DIRECTLY as a claim (a second credential the cookie's security stamp below never reaches), and
        // is accepted on hash + not-revoked + not-expired alone — so without this, a member who kept their
        // sa_… secret would keep reading the whole pantry after their cookie dies, until a remaining member
        // happened to revoke it by hand. This is the eviction gap CLAUDE.md item 12 closed for cookies,
        // reopened by the GraphQL token; removal must reach both credentials.
        await db.ApiTokens
            .Where(t => t.CreatedByUserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.Now), ct);

        await tx.CommitAsync(ct);

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
