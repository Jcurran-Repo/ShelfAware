using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>The whole tenancy hand-off: a valid bearer token becomes a principal carrying the household
/// claim (the same claim the cookie path uses, so CurrentHousehold + IHouseholdDbFactory scope for
/// free), and a missing / non-bearer / unknown / revoked / expired token becomes a bare 401 with no
/// login redirect.</summary>
public class ApiTokenAuthenticationHandlerTests : IDisposable
{
    private readonly TestAuthDb _authDb = new();

    public void Dispose() => _authDb.Dispose();

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private Task<ApiTokenMint> MintAsync(
        string household = "hh-a", string user = "user-7", DateTimeOffset? expiresAt = null) =>
        new ApiTokenService(_authDb).CreateAsync(household, user, "script", DateTimeOffset.Now, expiresAt);

    private async Task<(AuthenticateResult Result, DefaultHttpContext Context, ApiTokenAuthenticationHandler Handler)>
        RunAsync(string? authorizationHeader)
    {
        var handler = new ApiTokenAuthenticationHandler(
            new Monitor<ApiTokenAuthenticationOptions>(new ApiTokenAuthenticationOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            new ApiTokenService(_authDb));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream(); // so a challenge body can be read back
        if (authorizationHeader is not null) context.Request.Headers.Authorization = authorizationHeader;

        var scheme = new AuthenticationScheme(
            ApiTokenAuthenticationHandler.SchemeName, null, typeof(ApiTokenAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);
        var result = await handler.AuthenticateAsync();
        return (result, context, handler);
    }

    [Fact]
    public async Task A_valid_token_authenticates_with_the_household_claim()
    {
        var mint = await MintAsync(household: "hh-a", user: "user-7");

        var (result, _, _) = await RunAsync($"Bearer {mint.Secret}");

        Assert.True(result.Succeeded);
        Assert.True(result.Principal!.Identity!.IsAuthenticated);
        Assert.Equal("hh-a", result.Principal.FindFirst(HouseholdClaimsPrincipalFactory.HouseholdClaim)?.Value);
        Assert.Equal("user-7", result.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal(mint.Token.Id.ToString(), result.Principal.FindFirst(ApiTokenAuthenticationHandler.TokenIdClaim)?.Value);
    }

    [Fact]
    public async Task The_scheme_keyword_is_case_insensitive()
    {
        var mint = await MintAsync();

        var (result, _, _) = await RunAsync($"bearer {mint.Secret}"); // lowercase, per RFC 7235

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_missing_header_produces_no_result_so_the_endpoint_can_challenge()
    {
        var (result, _, _) = await RunAsync(authorizationHeader: null);

        Assert.False(result.Succeeded);
        Assert.True(result.None); // "no credential presented", not an authentication failure
    }

    [Fact]
    public async Task A_non_bearer_header_produces_no_result()
    {
        var (result, _, _) = await RunAsync("Basic dXNlcjpwYXNz");

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task An_unknown_token_fails()
    {
        var (result, _, _) = await RunAsync("Bearer sa_this-was-never-minted");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task A_revoked_token_fails()
    {
        var mint = await MintAsync();
        await new ApiTokenService(_authDb).RevokeAsync(mint.Token.Id, "hh-a", DateTimeOffset.Now);

        var (result, _, _) = await RunAsync($"Bearer {mint.Secret}");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task An_expired_token_fails()
    {
        var mint = await MintAsync(expiresAt: DateTimeOffset.Now.AddDays(-1));

        var (result, _, _) = await RunAsync($"Bearer {mint.Secret}");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task The_challenge_writes_a_401_with_a_json_body_and_no_login_redirect()
    {
        var (_, context, handler) = await RunAsync(authorizationHeader: null);

        await handler.ChallengeAsync(properties: null);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location")); // no redirect to a sign-in page

        // The body is load-bearing, not cosmetic: an EMPTY error response is what UseStatusCodePages
        // re-executes (method preserved) into a misleading 400 on a POST. A non-empty JSON body starts
        // the response so StatusCodePages leaves the real 401 alone.
        Assert.StartsWith("application/json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("Unauthorized", body);
    }
}
