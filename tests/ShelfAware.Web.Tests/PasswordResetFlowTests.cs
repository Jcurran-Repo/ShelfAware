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
/// The password-reset flow at the layer the two /Account pages drive: token generation, the pages'
/// exact Base64Url transport encoding, ResetPasswordAsync, and the properties the pages' copy
/// promises out loud (single use, other-session eviction, policy-vs-token error separation). The
/// static-SSR Account pages themselves have no harness — none of them do; bUnit can't drive their
/// form posts — so this layer is where the flow is pinned and the markup is live-verified instead.
/// </summary>
public sealed class PasswordResetFlowTests : IDisposable
{
    private readonly TestAuthDb _db = new();
    private readonly AuthDbContext _context;
    private readonly UserManager<AppUser> _users;

    public PasswordResetFlowTests()
    {
        _context = _db.CreateDbContext();
        // Mirrors Program.cs: 10+ characters, no composition rules — so a policy failure here
        // fails for the same reason it would in production.
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
        // Production gets this from AddDefaultTokenProviders(); a hand-built manager starts with
        // an EMPTY provider map and GeneratePasswordResetTokenAsync would throw outright.
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

    private async Task<AppUser> UserAsync(string email = "wife@example.test", string password = "original-pass-10")
    {
        var user = new AppUser { UserName = email, Email = email };
        var created = await _users.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        return user;
    }

    [Fact]
    public async Task A_generated_token_survives_the_pages_url_encoding_and_resets_the_password()
    {
        var user = await UserAsync();
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        // The exact transport the pages use: ForgotPassword Base64Url-encodes the token into the
        // emailed link, ResetPassword decodes it back before handing it to Identity.
        var wire = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(wire));

        var result = await _users.ResetPasswordAsync(user, decoded, "brand-new-pass-12");

        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        Assert.True(await _users.CheckPasswordAsync(user, "brand-new-pass-12"));
        Assert.False(await _users.CheckPasswordAsync(user, "original-pass-10"));
    }

    [Fact]
    public async Task A_tampered_token_is_refused_as_InvalidToken_and_the_password_stands()
    {
        var user = await UserAsync();

        var result = await _users.ResetPasswordAsync(user, "not-a-real-token", "brand-new-pass-12");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == nameof(IdentityErrorDescriber.InvalidToken));
        Assert.True(await _users.CheckPasswordAsync(user, "original-pass-10"));
    }

    [Fact]
    public async Task A_policy_violating_password_reports_the_policy_not_the_token()
    {
        // The ResetPassword page shows policy complaints verbatim but collapses InvalidToken into
        // its generic bad-link sentence — a split that only works if Identity keeps the two error
        // families distinct on one call.
        var user = await UserAsync();
        var token = await _users.GeneratePasswordResetTokenAsync(user);

        var result = await _users.ResetPasswordAsync(user, token, "short");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == nameof(IdentityErrorDescriber.PasswordTooShort));
        Assert.DoesNotContain(result.Errors, e => e.Code == nameof(IdentityErrorDescriber.InvalidToken));
    }

    [Fact]
    public async Task A_successful_reset_rotates_the_security_stamp_so_other_sessions_die()
    {
        // The page comments promise that a reset signs out whoever knew the old password; that
        // promise rides entirely on the stamp rotating (the 5-minute revalidation does the rest).
        var user = await UserAsync();
        var before = await _users.GetSecurityStampAsync(user);
        var token = await _users.GeneratePasswordResetTokenAsync(user);

        Assert.True((await _users.ResetPasswordAsync(user, token, "brand-new-pass-12")).Succeeded);

        Assert.NotEqual(before, await _users.GetSecurityStampAsync(user));
    }

    [Fact]
    public async Task A_used_token_stops_working()
    {
        // The email says "stops working once used" — true because a successful reset rotates the
        // security stamp the token was minted against, not because anything tracks usage.
        var user = await UserAsync();
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        Assert.True((await _users.ResetPasswordAsync(user, token, "brand-new-pass-12")).Succeeded);

        var again = await _users.ResetPasswordAsync(user, token, "third-password-13");

        Assert.False(again.Succeeded);
        Assert.Contains(again.Errors, e => e.Code == nameof(IdentityErrorDescriber.InvalidToken));
        Assert.True(await _users.CheckPasswordAsync(user, "brand-new-pass-12"));
    }
}
