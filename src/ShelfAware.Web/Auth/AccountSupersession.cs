using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ShelfAware.Web.Auth;

/// <summary>Supersedes an UNCONFIRMED placeholder account when its email is registered again — so a fresh
/// registration gives the registrant a new account in the household THEY choose, not one they were
/// pre-registered into by someone else.
///
/// ⚠️ THIS IS A PARTIAL FIX for the pre-registration hijack, and the complete fix is a HIGH-PRIORITY
/// follow-up — see the "PRE-HIJACK — COMPLETE FIX STILL OWED" note below.
///
/// The hijack: on a confirmation-required box, an attacker can register <c>victim@x</c> into the attacker's
/// OWN household via an invite code (invite-joins are exempt from the daily cap), leaving an unconfirmed
/// account that sits in the attacker's household. When the victim later takes the account over (confirm, or
/// reset — which now also confirms), they inherit that household and their receipts become visible to the
/// attacker.
///
/// What this DOES close: the RELIABLE version (pre-register, then wait). The victim's own registration lands
/// here first and deletes the placeholder, so the victim gets a fresh account in their own household.
///
/// ⚠️ PRE-HIJACK — COMPLETE FIX STILL OWED (do this ASAP; tracked as a spawned audit task): a NARROW residual
/// race remains — an attacker who re-registers <c>victim@x</c> in the brief window AFTER the victim registers
/// but BEFORE they confirm supersedes the victim's account and re-plants it in the attacker's household. It's
/// unreliable (the victim then holds two confirmation links and is only hijacked if they click the
/// attacker's, since their own is now dead), but it's real, and it exists because the household is assigned
/// at REGISTRATION — before anyone has proven they control the email. The complete fix removes that premise:
/// on a confirmation-required box, an account gets NO household at registration; the household is
/// created/joined AFTER confirmation at the existing ChooseHousehold step, by the person who proved inbox
/// control. That is a registration-flow redesign (it cascades into the account-creation cap and
/// Auth:AllowRegistration, both of which currently key off a household choice made at registration), which is
/// why it is its own focused change rather than rushed in here.</summary>
public sealed class AccountSupersession(
    AuthDbContext db, UserManager<AppUser> users, ILogger<AccountSupersession> logger)
{
    /// <summary>Delete an unconfirmed placeholder for <paramref name="email"/> (and its now-empty household),
    /// if one exists. A CONFIRMED account is real and is left untouched (the caller then handles it as a
    /// duplicate). Runs on the caller's scoped <see cref="AuthDbContext"/>, so it participates in the
    /// registration transaction and rolls back with it.</summary>
    public async Task SupersedeUnconfirmedPlaceholderAsync(string email, CancellationToken ct = default)
    {
        var existing = await users.FindByEmailAsync(email);
        if (existing is null || await users.IsEmailConfirmedAsync(existing))
        {
            return; // nothing pending, or a real account — leave it for the caller's duplicate handling
        }

        var householdId = existing.HouseholdId;
        var deleted = await users.DeleteAsync(existing);
        if (!deleted.Succeeded)
        {
            // Couldn't remove it — abort superseding; the caller's duplicate path handles it safely.
            logger.LogWarning("Couldn't supersede an unconfirmed placeholder: {Errors}",
                string.Join("; ", deleted.Errors.Select(e => e.Description)));
            return;
        }
        logger.LogInformation("Superseded an unconfirmed placeholder account on re-registration.");

        // If the placeholder was the ONLY member of its household, remove that now-empty household (and any
        // welcome grant) so a superseded registration leaves no orphan. An invite-joined placeholder (the
        // hijack case) leaves its household populated by its other members, so this only fires for a
        // self-created one. Safe: an unconfirmed account never signed in, so its household has no pantry data.
        if (householdId is not null && !await db.Users.AnyAsync(u => u.HouseholdId == householdId, ct))
        {
            await db.CreditLedger.Where(e => e.HouseholdId == householdId).ExecuteDeleteAsync(ct);
            await db.Households.Where(h => h.Id == householdId).ExecuteDeleteAsync(ct);
            logger.LogInformation("Removed the now-empty household of a superseded placeholder.");
        }
    }
}
