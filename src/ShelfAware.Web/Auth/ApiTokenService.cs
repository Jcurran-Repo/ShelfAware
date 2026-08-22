using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace ShelfAware.Web.Auth;

/// <summary>The one raw secret a mint produces, alongside the stored record. The secret is returned to
/// the caller exactly once (to show the person) and then only its hash survives — so this pairing exists
/// only for the length of the create call.</summary>
public sealed record ApiTokenMint(ApiToken Token, string Secret);

/// <summary>Mint / validate / list / revoke for GraphQL API tokens — the ONE definition of those
/// operations, shared by the Settings UI (mint, list, revoke), the auth handler (validate), and the
/// delete-my-data flow. Talks to auth.db through the RAW <see cref="IDbContextFactory{AuthDbContext}"/>
/// (like <c>ErrorLogStore</c>): these are credentials, not pantry data, and the lookup happens before a
/// household is known, so there is no query filter to go through — the household scope is enforced HERE,
/// explicitly, on the operations that need it (list/revoke take the caller's household id).</summary>
public sealed class ApiTokenService(IDbContextFactory<AuthDbContext> dbFactory)
{
    /// <summary>Every raw secret starts with this, so a leaked string is recognisable as a Shelf Aware
    /// API token (the GitHub-<c>ghp_</c> convention) and the display prefix is human-identifiable.</summary>
    public const string SecretPrefix = "sa_";

    // 32 CSPRNG bytes = 256 bits of entropy — far past any brute-force surface, so a plain hash (below)
    // is the correct storage, no slow KDF.
    private const int SecretBytes = 32;

    // "sa_" + the first 8 base64url chars. Enough to tell tokens apart in the list; a negligible slice of
    // the 256 random bits, so showing it leaks nothing that matters.
    private const int PrefixLength = 11;

    /// <summary>Mint a token for a household: generate a fresh CSPRNG secret, store only its hash + a
    /// display prefix, and return the secret ONCE for the caller to show the person. <paramref name="now"/>
    /// stamps <see cref="ApiToken.CreatedAt"/> and, when given, <paramref name="expiresAt"/> is the
    /// absolute moment the token stops working (null = no expiry).</summary>
    public async Task<ApiTokenMint> CreateAsync(
        string householdId, string createdByUserId, string name,
        DateTimeOffset now, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
    {
        var secret = SecretPrefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretBytes));
        var token = new ApiToken
        {
            HouseholdId = householdId,
            CreatedByUserId = createdByUserId,
            Name = name.Trim(),
            TokenHash = HashOf(secret),
            Prefix = secret[..PrefixLength],
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return new ApiTokenMint(token, secret);
    }

    /// <summary>Resolve a presented secret to its live token, or null if it's blank / unknown / revoked /
    /// expired. On success it stamps <see cref="ApiToken.LastUsedAt"/> = <paramref name="now"/>. This is
    /// the authentication step the handler runs on every API request.</summary>
    public async Task<ApiToken?> ValidateAsync(string? presentedSecret, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presentedSecret)) return null;

        var hash = HashOf(presentedSecret);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Looked up by HASH, which the caller has no preimage control over, so there's no secret-timing
        // side channel to worry about here (unlike comparing the raw secret directly).
        var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || !token.IsUsable(now)) return null;

        token.LastUsedAt = now;
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>This household's tokens, newest first — for the Settings list. Ordered client-side
    /// because SQLite can't <c>ORDER BY</c> a <c>DateTimeOffset</c> in SQL (the <c>ErrorLogStore</c>
    /// constraint); the per-household count is tiny, so the fetch-then-sort is free.</summary>
    public async Task<List<ApiToken>> ListAsync(string householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ApiTokens.AsNoTracking().Where(t => t.HouseholdId == householdId).ToListAsync(ct);
        return [.. rows.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)];
    }

    /// <summary>Revoke one of THIS household's tokens (immediate + permanent). Scoped to
    /// <paramref name="householdId"/> so one household can never revoke another's token by id — auth.db
    /// has no query filter, so this is the tenancy guard, done in the WHERE. A conditional update on
    /// <c>RevokedAt == null</c>, so re-revoking an already-revoked token returns false rather than
    /// re-stamping. Returns whether a live token was actually revoked.</summary>
    public async Task<bool> RevokeAsync(int id, string householdId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiTokens
            .Where(t => t.Id == id && t.HouseholdId == householdId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct) > 0;
    }

    /// <summary>SHA-256 (hex) of the raw secret — the same shape <c>ErrorLogStore.FingerprintOf</c> uses.
    /// Deterministic, so the handler hashes a presented secret and matches it against the stored hash.</summary>
    private static string HashOf(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
