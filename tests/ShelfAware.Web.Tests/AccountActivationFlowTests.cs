using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The new-account ACTIVATION flow at the layer the /Account pages drive. On a confirmation-required box
/// (<c>Auth:RequireEmailConfirmation</c>) registration creates a PASSWORDLESS account
/// (RegisterForConfirmationAsync), and the emailed link sets the password through the reset flow — which is
/// the anti-hijack property: whoever merely typed the email at sign-up (possibly NOT the inbox-holder) can't
/// sign in, because there is no password to try; only the person who receives the link can establish one.
///
/// Pins: a passwordless account can't be signed into; a reset token sets the FIRST password on a passwordless
/// account (and works there at all — the token rides the security stamp, not a password); the ResetPassword
/// page's confirm-on-set is what activates it under RequireConfirmedAccount; a re-registration is a clean
/// duplicate with no household to inherit; and an activated account still has no household. The static-SSR
/// Account pages have no bUnit harness (none do), so this is where the flow is pinned and the markup is
/// live-verified. Mirrors <see cref="PasswordResetFlowTests"/>, which covers the reset token's transport.
/// </summary>
public sealed class AccountActivationFlowTests : IDisposable
{
    private readonly TestAuthDb _db = new();
    private readonly AuthDbContext _context;
    private readonly UserManager<AppUser> _users;
    private readonly IdentityOptions _identityOptions = new();

    public AccountActivationFlowTests()
    {
        _context = _db.CreateDbContext();
        _identityOptions.Password.RequiredLength = 10;
        _identityOptions.Password.RequireNonAlphanumeric = false;
        _identityOptions.Password.RequireUppercase = false;
        _identityOptions.Password.RequireLowercase = false;
        _identityOptions.Password.RequireDigit = false;
        // The demo box's gate: an unconfirmed account may not sign in. Read by the SignInManager below.
        _identityOptions.SignIn.RequireConfirmedAccount = true;
        // Production pairs UserName with Email and requires the email unique — the invariant that makes a
        // re-registration of an existing address a duplicate (the anti-hijack property below leans on it).
        _identityOptions.User.RequireUniqueEmail = true;
        _users = new UserManager<AppUser>(
            new UserStore<AppUser>(_context), Options.Create(_identityOptions),
            // A UserValidator runs the username/email uniqueness check and returns a clean DuplicateUserName
            // result — without it a re-registration hits the DB unique index and THROWS instead. Production
            // registers the default validators.
            new PasswordHasher<AppUser>(), [new UserValidator<AppUser>()], [new PasswordValidator<AppUser>()],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<AppUser>>.Instance);
        // Production gets this from AddDefaultTokenProviders(); the DataProtector "Default" provider is what
        // GeneratePasswordResetTokenAsync uses, so a hand-built manager needs it registered or the generate
        // call throws outright.
        _users.RegisterTokenProvider(TokenOptions.DefaultProvider,
            new DataProtectorTokenProvider<AppUser>(
                new EphemeralDataProtectionProvider(),
                Options.Create(new DataProtectionTokenProviderOptions()),
                NullLogger<DataProtectorTokenProvider<AppUser>>.Instance));
    }

    public void Dispose()
    {
        _users.Dispose();
        _context.Dispose();
        _db.Dispose();
    }

    /// <summary>A brand-new account exactly as RegisterForConfirmationAsync makes it: created with NO
    /// password (so it can't be signed into) and unconfirmed.</summary>
    private async Task<AppUser> PasswordlessAccountAsync(string email = "visitor@example.test")
    {
        var user = new AppUser { UserName = email, Email = email };
        var created = await _users.CreateAsync(user); // NO password overload — the passwordless registration
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        Assert.False(await _users.HasPasswordAsync(user));       // the anti-hijack precondition
        Assert.False(await _users.IsEmailConfirmedAsync(user));
        return user;
    }

    [Fact]
    public async Task A_passwordless_account_cannot_be_signed_into_on_any_password()
    {
        // THE anti-hijack property of the activation model: registration sets no password, so whoever typed
        // the email at sign-up can't sign in — there's nothing to verify against. Only the person who
        // receives the emailed link can set a password (the next test). Without this, an attacker who
        // registered a victim's email and chose the password could sign in the moment the victim clicked the
        // link — the exact residual the reset-based activation closes.
        var user = await PasswordlessAccountAsync();
        // Confirm it first, so the block below is specifically the MISSING PASSWORD and not the unconfirmed
        // gate: even a CONFIRMED passwordless account has no hash to match, on ANY input.
        user.EmailConfirmed = true;
        await _users.UpdateAsync(user);
        var signIn = BuildSignInManager();

        Assert.False((await signIn.CheckPasswordSignInAsync(user, "attacker-chose-this", false)).Succeeded);
        Assert.False((await signIn.CheckPasswordSignInAsync(user, "another-guess-entirely", false)).Succeeded);
    }

    [Fact]
    public async Task The_emailed_link_sets_the_first_password_and_activates_a_passwordless_account()
    {
        // The activation link is a reset token on the passwordless account. It generates (the token rides the
        // security stamp, which exists without a password) and ResetPasswordAsync SETS the first password.
        var user = await PasswordlessAccountAsync();
        var token = await _users.GeneratePasswordResetTokenAsync(user);

        var set = await _users.ResetPasswordAsync(user, token, "brand-new-pass-10");
        Assert.True(set.Succeeded, string.Join(" ", set.Errors.Select(e => e.Description)));
        Assert.True(await _users.HasPasswordAsync(user));

        var signIn = BuildSignInManager();
        // Still blocked until confirmed — RequireConfirmedAccount runs before the password. The ResetPassword
        // PAGE flips EmailConfirmed on a successful set (that's what makes setting the password activate the
        // account); before that step the account is passworded-but-unconfirmed.
        Assert.True((await signIn.CheckPasswordSignInAsync(user, "brand-new-pass-10", false)).IsNotAllowed);

        user.EmailConfirmed = true; // what ResetPassword.razor does on success
        await _users.UpdateAsync(user);

        // Now activated: the inbox-holder's own password checks out, a wrong one does not.
        Assert.True((await signIn.CheckPasswordSignInAsync(user, "brand-new-pass-10", false)).Succeeded);
        Assert.False((await signIn.CheckPasswordSignInAsync(user, "not-the-password", false)).Succeeded);
    }

    [Fact]
    public async Task An_activated_account_still_has_no_household_until_the_chooser_assigns_one()
    {
        // Deferral: activation establishes a credential and confirms the address, but assigns NO household —
        // the chooser does that afterwards. So re-registering someone's email can pre-plant nothing they
        // inherit by activating: there is simply no household on the account to inherit.
        var user = await PasswordlessAccountAsync();
        Assert.Null(user.HouseholdId);

        var token = await _users.GeneratePasswordResetTokenAsync(user);
        Assert.True((await _users.ResetPasswordAsync(user, token, "brand-new-pass-10")).Succeeded);
        user.EmailConfirmed = true;
        await _users.UpdateAsync(user);

        await _context.Entry(user).ReloadAsync();
        Assert.True(await _users.IsEmailConfirmedAsync(user));
        Assert.Null(user.HouseholdId); // activation grants no household — the chooser does, later
    }

    [Fact]
    public async Task Re_registering_an_existing_email_is_refused_as_a_duplicate_with_no_household_to_inherit()
    {
        // A re-registration of an address that already has an account is refused by CreateAsync (unique
        // username/email) — the page turns that into the enumeration-safe "check your inbox" plus an
        // already-registered heads-up. And because the existing account carries no household (and no password),
        // the attempt can plant nothing and grants no access regardless of who later activates it.
        var existing = await PasswordlessAccountAsync("owner@example.test");
        Assert.Null(existing.HouseholdId);

        var second = new AppUser { UserName = "owner@example.test", Email = "owner@example.test" };
        var result = await _users.CreateAsync(second);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e =>
            e.Code == nameof(IdentityErrorDescriber.DuplicateUserName)
            || e.Code == nameof(IdentityErrorDescriber.DuplicateEmail));
    }

    [Fact]
    public async Task An_unactivated_account_is_blocked_at_sign_in_on_ANY_password_so_login_must_stay_generic()
    {
        // WHY the Login page gives an unconfirmed account the SAME generic "invalid email or password" as
        // every other failed sign-in (and offers "set your password" only as a standing, deployment-wide
        // link, never a per-attempt one): Identity runs the RequireConfirmedAccount gate in PreSignInCheck,
        // BEFORE it verifies the password, so an unactivated account returns IsNotAllowed on ANY password. A
        // message that fired on IsNotAllowed alone would leak which addresses have unactivated accounts, and
        // keying it off password-correctness would trade that for an unthrottled password-guess oracle — so
        // the page reveals nothing here. If Identity ever verified the password first (a wrong password
        // reading as Failed below), the reasoning would need revisiting; this test is the tripwire.
        var user = await PasswordlessAccountAsync();
        var signIn = BuildSignInManager();

        // Unactivated (unconfirmed) → blocked regardless of password, because the gate is checked first...
        Assert.True((await signIn.CheckPasswordSignInAsync(user, "brand-new-pass-10", false)).IsNotAllowed);
        Assert.True((await signIn.CheckPasswordSignInAsync(user, "anything-else-999", false)).IsNotAllowed);

        // ...activate it (set the password via the reset token, then confirm as the page does)...
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        Assert.True((await _users.ResetPasswordAsync(user, token, "brand-new-pass-10")).Succeeded);
        user.EmailConfirmed = true;
        await _users.UpdateAsync(user);

        // ...and now a WRONG password is a plain Failed (NOT IsNotAllowed) — the state Login's generic
        // message already covers, so IsNotAllowed is specifically the not-yet-activated signal.
        var activatedWrong = await signIn.CheckPasswordSignInAsync(user, "wrong-pass-999", false);
        Assert.False(activatedWrong.IsNotAllowed);
        Assert.False(activatedWrong.Succeeded);
    }

    /// <summary>A SignInManager wired minimally for the probes above. Every case returns before an actual
    /// sign-in (PreSignInCheck bails, the password is wrong, or there is no password), so a bare
    /// <see cref="DefaultHttpContext"/> with no authentication services is enough — nothing calls
    /// HttpContext.SignInAsync.</summary>
    private SignInManager<AppUser> BuildSignInManager()
    {
        var options = Options.Create(_identityOptions);
        return new SignInManager<AppUser>(
            _users,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new UserClaimsPrincipalFactory<AppUser>(_users, options),
            options,
            NullLogger<SignInManager<AppUser>>.Instance,
            new AuthenticationSchemeProvider(Options.Create(new AuthenticationOptions())),
            new DefaultUserConfirmation<AppUser>());
    }
}
