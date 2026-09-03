using System.Text;
using Microsoft.AspNetCore.DataProtection;
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

    public EmailConfirmationFlowTests()
    {
        _context = _db.CreateDbContext();
        var identity = new IdentityOptions();
        identity.Password.RequiredLength = 10;
        identity.Password.RequireNonAlphanumeric = false;
        identity.Password.RequireUppercase = false;
        identity.Password.RequireLowercase = false;
        identity.Password.RequireDigit = false;
        _users = new UserManager<AppUser>(
            new UserStore<AppUser>(_context), Options.Create(identity),
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
}
