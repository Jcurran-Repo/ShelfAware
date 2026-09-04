using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// <see cref="AppUser.NewToday"/> is the single production account-construction path, so the
/// account-creation cap's <see cref="AppUser.CreatedOn"/> stamp is centralised in one place. These pin
/// that it stamps today (so a new account counts toward the cap) and sets the confirmation state each
/// creation site needs.
/// </summary>
public class AppUserTests
{
    [Fact]
    public void NewToday_stamps_todays_date_and_pairs_username_with_email()
    {
        var user = AppUser.NewToday("visitor@example.test");

        Assert.Equal("visitor@example.test", user.Email);
        Assert.Equal("visitor@example.test", user.UserName); // the app's username == email invariant
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), user.CreatedOn);
    }

    [Fact]
    public void NewToday_defaults_to_unconfirmed_for_a_self_registered_account()
    {
        // The password-registration path: the account must confirm its email where the flag is on.
        Assert.False(AppUser.NewToday("visitor@example.test").EmailConfirmed);
    }

    [Fact]
    public void NewToday_can_start_confirmed_for_a_provider_asserted_or_dev_account()
    {
        // ExternalLogin (OAuth-asserted) and DevAuth create already-confirmed accounts.
        Assert.True(AppUser.NewToday("visitor@example.test", emailConfirmed: true).EmailConfirmed);
    }
}
