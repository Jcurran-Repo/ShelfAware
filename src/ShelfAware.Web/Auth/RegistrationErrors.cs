using Microsoft.AspNetCore.Identity;

namespace ShelfAware.Web.Auth;

/// <summary>Classifies the errors from a failed <c>UserManager.CreateAsync</c> so the Register page can be
/// enumeration-safe about a duplicate email.
///
/// Revealing "that email is already taken" confirms an address has an account. On a box whose posture is
/// anti-enumeration (<c>Auth:RequireEmailConfirmation</c>), a registration for an already-taken email must
/// return the SAME response as a real one — so the page needs to tell a PURE duplicate (hide it, respond as
/// success) from a duplicate that rides alongside a real problem the user must fix, like a weak password
/// (show only that problem, with the duplicate stripped — a weak password fails identically whether or not
/// the email exists, so it leaks nothing). Lives here, not in the page, so both branches are testable.</summary>
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
