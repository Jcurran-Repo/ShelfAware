using Microsoft.AspNetCore.Identity;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The enumeration-safe classification of a failed registration: a PURE duplicate email is hidden behind
/// the normal "check your inbox" response, while a duplicate riding alongside a real problem (a weak
/// password) shows only that problem with the duplicate stripped — so "email already taken" never confirms
/// an address exists, and a weak password fails identically whether the email is new or taken.
/// </summary>
public class RegistrationErrorsTests
{
    private static IdentityError Duplicate() =>
        new() { Code = nameof(IdentityErrorDescriber.DuplicateEmail), Description = "Email 'x@y.test' is already taken." };

    private static IdentityError DuplicateName() =>
        new() { Code = nameof(IdentityErrorDescriber.DuplicateUserName), Description = "Username 'x@y.test' is already taken." };

    private static IdentityError WeakPassword() =>
        new() { Code = nameof(IdentityErrorDescriber.PasswordTooShort), Description = "Passwords must be at least 10 characters." };

    [Fact]
    public void A_pure_duplicate_is_duplicate_only()
    {
        Assert.True(RegistrationErrors.IsDuplicateOnly(IdentityResult.Failed(Duplicate())));
        Assert.True(RegistrationErrors.IsDuplicateOnly(IdentityResult.Failed(DuplicateName())));
        // Both duplicate codes together (email == username in this app) still count as duplicate-only.
        Assert.True(RegistrationErrors.IsDuplicateOnly(IdentityResult.Failed(Duplicate(), DuplicateName())));
    }

    [Fact]
    public void A_duplicate_beside_a_real_problem_is_not_duplicate_only()
    {
        // A weak password is present too, so this is NOT hidden — the user must fix the password.
        Assert.False(RegistrationErrors.IsDuplicateOnly(IdentityResult.Failed(Duplicate(), WeakPassword())));
    }

    [Fact]
    public void A_non_duplicate_failure_is_not_duplicate_only()
    {
        Assert.False(RegistrationErrors.IsDuplicateOnly(IdentityResult.Failed(WeakPassword())));
        Assert.False(RegistrationErrors.IsDuplicateOnly(IdentityResult.Failed())); // no errors at all
    }

    [Fact]
    public void Excluding_duplicate_strips_only_the_duplicate_errors()
    {
        var shown = RegistrationErrors.ExcludingDuplicate(
            IdentityResult.Failed(Duplicate(), WeakPassword())).ToList();

        // The password complaint survives (the user must act on it); the duplicate is gone, so it can't
        // reveal existence beside it.
        var only = Assert.Single(shown);
        Assert.Equal(nameof(IdentityErrorDescriber.PasswordTooShort), only.Code);
    }

    [Fact]
    public void Excluding_duplicate_from_a_pure_duplicate_leaves_nothing_to_show()
    {
        Assert.Empty(RegistrationErrors.ExcludingDuplicate(IdentityResult.Failed(Duplicate())));
    }
}
