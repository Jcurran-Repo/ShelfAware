using Microsoft.AspNetCore.Identity;

namespace ShelfAware.Web.Auth;

/// <summary>Classifies the errors from a failed <c>UserManager.CreateAsync</c> so the Register page can be
/// enumeration-safe about a duplicate email.
///
/// Revealing "that email is already taken" confirms an address has an account. On a box whose posture is
/// anti-enumeration (<c>Auth:RequireEmailConfirmation</c>), a registration for an already-taken email must
/// return the SAME response as a real one — so the page needs to tell a PURE duplicate (hide it, respond as
/// success) from a duplicate that rides alongside a real problem the user must fix, like a weak password
/// (show only that problem, with the duplicate stripped). Lives here, not in the page, so both branches are
/// testable.
///
/// ⚠️ Note the "duplicate beside a weak password" case cannot actually arise today: <c>CreateAsync</c>
/// validates the password FIRST and returns before it ever reaches the uniqueness check, so a weak password
/// yields a password error with no duplicate error alongside it (and a weak password fails identically for a
/// new or an existing email, leaking nothing either way). <see cref="ExcludingDuplicate"/> is therefore
/// defence-in-depth against a future change to Identity's validation order, not a reachable path — kept
/// because a wrong assumption there would be a silent enumeration leak.</summary>
public static class RegistrationErrors
{
    private static bool IsDuplicateAccount(IdentityError e) =>
        e.Code is nameof(IdentityErrorDescriber.DuplicateUserName) or nameof(IdentityErrorDescriber.DuplicateEmail);

    /// <summary>True when the failure is ONLY a duplicate email/username — a valid registration for an
    /// address that already has an account. This is the case the page hides behind the normal "check your
    /// inbox" response.</summary>
    public static bool IsDuplicateOnly(IdentityResult result) =>
        result.Errors.Any() && result.Errors.All(IsDuplicateAccount);

    /// <summary>The errors to SHOW the user — everything except a duplicate-account error. Used where the
    /// box hides existence, so "email already taken" can't ride out alongside a password complaint. On a box
    /// that doesn't hide existence the page shows the raw errors instead.</summary>
    public static IEnumerable<IdentityError> ExcludingDuplicate(IdentityResult result) =>
        result.Errors.Where(e => !IsDuplicateAccount(e));
}
