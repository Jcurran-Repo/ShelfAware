using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The email-confirmation flow at the layer the /Account pages drive: token generation, the pages' exact
/// Base64Url transport (Register/ResendEmailConfirmation build the link via <see cref="AccountLinks"/>,
/// ConfirmEmail decodes it), ConfirmEmailAsync, and the input Identity's sign-in gate reads
/// (IsEmailConfirmedAsync). The static-SSR Account pages themselves have no harness — none of them do;
/// bUnit can't drive their form posts — so this is where the flow is pinned and the markup is live-verified.
/// Mirrors <see cref="PasswordResetFlowTests"/>, its sibling.
/// </summary>
public sealed class EmailConfirmationFlowTests : IDisposable
{
    private readonly TestAuthDb _db = new();
    private readonly AuthDbContext _context;
    private readonly UserManager<AppUser> _users;
    private readonly IdentityOptions _identityOptions = new();

    public EmailConfirmationFlowTests()
    {
        _context = _db.CreateDbContext();
        _identityOptions.Password.RequiredLength = 10;
        _identityOptions.Password.RequireNonAlphanumeric = false;
        _identityOptions.Password.RequireUppercase = false;
        _identityOptions.Password.RequireLowercase = false;
        _identityOptions.Password.RequireDigit = false;
        // The demo box's gate: an unconfirmed account may not sign in. Only the SignInManager reads this
        // (the confirm-token tests below are unaffected by it), so it's on for the whole class.
        _identityOptions.SignIn.RequireConfirmedAccount = true;
        _users = new UserManager<AppUser>(
            new UserStore<AppUser>(_context), Options.Create(_identityOptions),
            new PasswordHasher<AppUser>(), [], [new PasswordValidator<AppUser>()],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<AppUser>>.Instance);
        // Production gets this from AddDefaultTokenProviders(); the DataProtector "Default" provider is
        // what GenerateEmailConfirmationTokenAsync uses (EmailConfirmationTokenProvider defaults to it), so
        // a hand-built manager needs it registered or the generate call throws outright.
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

    private async Task<AppUser> UnconfirmedUserAsync(string email = "visitor@example.test")
    {
        var user = new AppUser { UserName = email, Email = email };
        var created = await _users.CreateAsync(user, "original-pass-10");
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        // A fresh account is unconfirmed — the state the demo box's gate refuses to sign in.
        Assert.False(await _users.IsEmailConfirmedAsync(user));
        return user;
    }

    [Fact]
    public async Task A_generated_token_survives_the_pages_url_encoding_and_confirms_the_email()
    {
        var user = await UnconfirmedUserAsync();
        var token = await _users.GenerateEmailConfirmationTokenAsync(user);
        // The exact transport the pages use: AccountLinks Base64Url-encodes the token into the emailed
        // link, ConfirmEmail decodes it back before handing it to Identity.
        var wire = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(wire));

        var result = await _users.ConfirmEmailAsync(user, decoded);

        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        // IsEmailConfirmedAsync is exactly what RequireConfirmedAccount reads to allow sign-in.
        Assert.True(await _users.IsEmailConfirmedAsync(user));
    }

    [Fact]
    public async Task A_tampered_token_is_refused_as_InvalidToken_and_the_email_stays_unconfirmed()
    {
        var user = await UnconfirmedUserAsync();

        var result = await _users.ConfirmEmailAsync(user, "not-a-real-token");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == nameof(IdentityErrorDescriber.InvalidToken));
        Assert.False(await _users.IsEmailConfirmedAsync(user));
    }

    [Fact]
    public async Task One_accounts_token_does_not_confirm_another()
    {
        // The token is bound to the user it was minted for, so a link for one account can't confirm
        // another even with a valid-looking code — the confirm URL carries the userId, and the two must
        // agree for Identity to accept it.
        var mine = await UnconfirmedUserAsync("mine@example.test");
        var theirs = await UnconfirmedUserAsync("theirs@example.test");
        var myToken = await _users.GenerateEmailConfirmationTokenAsync(mine);

        var result = await _users.ConfirmEmailAsync(theirs, myToken);

        Assert.False(result.Succeeded);
        Assert.False(await _users.IsEmailConfirmedAsync(theirs));
        Assert.False(await _users.IsEmailConfirmedAsync(mine));
    }

    [Fact]
    public async Task An_unconfirmed_account_is_blocked_at_sign_in_on_ANY_password_which_is_why_login_re_checks_it()
    {
        // The premise the Login page's enumeration guard rests on — and the fact this session's first
        // comment got WRONG (the pre-merge review probed it). Identity runs the RequireConfirmedAccount gate
        // in PreSignInCheck, BEFORE verifying the password, so an unconfirmed account returns IsNotAllowed on
        // ANY password. Surfacing the "confirm your email" hint on IsNotAllowed alone would therefore leak
        // which addresses have unconfirmed accounts to a prober who doesn't know the password — so Login
        // re-checks the password (CheckPasswordAsync) and only shows the hint when it's also correct.
        // If Identity ever verified the password first (making a wrong password read as Failed here), that
        // guard would become belt-and-braces rather than load-bearing; it should stay either way.
        var user = await UnconfirmedUserAsync(); // password "original-pass-10"
        var signIn = BuildSignInManager();

        // Right password, but unconfirmed → still blocked (the gate is checked before the password)...
        Assert.True((await signIn.PasswordSignInAsync(user, "original-pass-10", false, false)).IsNotAllowed);
        // ...and a WRONG password returns the IDENTICAL result, so IsNotAllowed cannot tell the two apart.
        Assert.True((await signIn.PasswordSignInAsync(user, "wrong-pass-999", false, false)).IsNotAllowed);

        // Once confirmed, a wrong password is a plain Failed (NOT IsNotAllowed) — the state Login's generic
        // "invalid email or password" already covers, which is why IsNotAllowed is specifically the
        // unconfirmed-account signal the guard is about.
        var token = await _users.GenerateEmailConfirmationTokenAsync(user);
        Assert.True((await _users.ConfirmEmailAsync(user, token)).Succeeded);
        var confirmedWrong = await signIn.PasswordSignInAsync(user, "wrong-pass-999", false, false);
        Assert.False(confirmedWrong.IsNotAllowed);
        Assert.False(confirmedWrong.Succeeded);
    }

    /// <summary>A SignInManager wired minimally for the failure-path probe above. Every case there returns
    /// before an actual sign-in (PreSignInCheck bails, or the password is wrong), so a bare
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
