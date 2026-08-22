using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ShelfAware.Web.Auth;

/// <summary>Options for the <see cref="ApiTokenAuthenticationHandler"/> scheme. No knobs today —
/// the type exists because <c>AddScheme&lt;TOptions, THandler&gt;</c> requires one.</summary>
public sealed class ApiTokenAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>Authenticates a plain HTTP request by its <c>Authorization: Bearer sa_…</c> header, for the
/// read-only GraphQL API. This is the ENTIRE tenancy integration: on success it builds a principal
/// carrying the household claim, and from there the existing machinery scopes everything — the
/// authorization policy that requires this scheme promotes the principal to <c>HttpContext.User</c>,
/// <c>CurrentHousehold</c> reads the same claim the cookie path reads, and <c>IHouseholdDbFactory</c>
/// stamps every context. No new query-filter bypass, no cross-household reach: a token grants exactly
/// one household, and a query for another's data can't even be expressed.
///
/// Registered ALONGSIDE the cookie/Identity schemes, never as the default — it runs only when the
/// GraphQL endpoint's policy names it. A missing/invalid/revoked/expired token becomes a bare 401 (the
/// <c>/api</c> convention — a program gets a status code, not the human login-page redirect).</summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<ApiTokenAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenService tokens)
    : AuthenticationHandler<ApiTokenAuthenticationOptions>(options, logger, encoder)
{
    /// <summary>The scheme name the GraphQL endpoint's policy requires.</summary>
    public const string SchemeName = "ApiToken";

    /// <summary>The authorization policy that requires this scheme + an authenticated token.</summary>
    public const string PolicyName = "GraphQLApi";

    /// <summary>Carries the authenticated token's id, so the rate limiter can partition per token
    /// (phase 5) rather than per IP (every prod caller shares one proxy address).</summary>
    public const string TokenIdClaim = "shelfaware:token";

    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No bearer credential presented → this scheme has nothing to say; let the challenge 401. NoResult
        // (not Fail) keeps it a "no credential" outcome rather than a logged authentication failure. The
        // scheme keyword is case-insensitive per RFC 7235.
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authorization) ||
            !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var presented = authorization[BearerPrefix.Length..].Trim();
        var token = await tokens.ValidateAsync(presented, DateTimeOffset.Now, Context.RequestAborted);
        if (token is null)
        {
            return AuthenticateResult.Fail("Invalid, revoked, or expired API token.");
        }

        // The household claim is what CurrentHousehold reads — the same claim the cookie factory bakes in,
        // so the tenant resolves with no further wiring. NameIdentifier records who minted the token.
        var identity = new ClaimsIdentity(
        [
            new Claim(HouseholdClaimsPrincipalFactory.HouseholdClaim, token.HouseholdId),
            new Claim(ClaimTypes.NameIdentifier, token.CreatedByUserId),
            new Claim(TokenIdClaim, token.Id.ToString(CultureInfo.InvariantCulture)),
        ], SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>A bare 401 — no HTML login redirect. A GraphQL client is a program, not a browser at a
    /// sign-in page (mirrors the cookie scheme's <c>/api</c> handling).</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
