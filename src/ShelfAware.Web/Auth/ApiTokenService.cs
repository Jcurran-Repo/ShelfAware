using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ShelfAware.Web.Auth;

/// <summary>The one raw secret a mint produces, alongside the stored record. The secret is returned to
/// the caller exactly once (to show the person) and then only its hash survives — so this pairing exists
/// only for the length of the create call.</summary>
public sealed record ApiTokenMint(ApiToken Token, string Secret);

/// <summary>Export-safe view of a token: everything a person's data export should say about it, and
/// NOTHING it shouldn't — no <see cref="ApiToken.TokenHash"/> (revealing it would let anyone with the
/// export impersonate the token, since the handler only ever has the hash to compare against).</summary>
public sealed record ApiTokenMetadata(
    string Name, string Prefix, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, DateTimeOffset? ExpiresAt);

/// <summary>What the owner's Settings list needs to render each token — the id (to revoke it) plus the
/// display fields, but NEVER the hash. So the credential's fingerprint never even enters the component's
/// render tree, matching the export path's discipline. (Distinct from <see cref="ApiTokenMetadata"/>,
/// which carries no id because an export never revokes.)</summary>
public sealed record ApiTokenSummary(
    int Id, string Name, string Prefix, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, DateTimeOffset? ExpiresAt);

/// <summary>Mint / validate / list / revoke for GraphQL API tokens — the ONE definition of those
/// operations, shared by the Settings UI (mint, list, revoke), the auth handler (validate), and the
/// delete-my-data flow. Talks to auth.db through the RAW <see cref="IDbContextFactory{AuthDbContext}"/>
/// (like <c>ErrorLogStore</c>): these are credentials, not pantry data, and the lookup happens before a
/// household is known, so there is no query filter to go through — the household scope is enforced HERE,
/// explicitly, on the operations that need it (list/revoke take the caller's household id).</summary>
public sealed class ApiTokenService(IDbContextFactory<AuthDbContext> dbFactory, ILogger<ApiTokenService>? logger = null)
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

        // Best-effort: LastUsedAt is a non-essential display field, and this runs on the auth hot path
        // against the SHARED auth.db (login, the security-stamp revalidation, the error log all live
        // there). A transient write failure — a SQLite busy lock under load, say — must NOT turn an
        // already-valid token into a 500. Stamp if we can; log and carry on if we can't.
        try
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Couldn't stamp LastUsedAt on an accepted API token; the token is still valid.");
        }
        return token;
    }

    /// <summary>This household's tokens, newest first — for the Settings list. Ordered client-side
    /// because SQLite can't <c>ORDER BY</c> a <c>DateTimeOffset</c> in SQL (the <c>ErrorLogStore</c>
    /// constraint); the per-household count is tiny, so the fetch-then-sort is free.</summary>
    public async Task<List<ApiTokenSummary>> ListAsync(string householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ApiTokens.AsNoTracking()
            .Where(t => t.HouseholdId == householdId)
            .Select(t => new ApiTokenSummary(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt, t.RevokedAt, t.ExpiresAt))
            .ToListAsync(ct);
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

    /// <summary>Export-safe metadata for a household's tokens (never the hash) — for "download my data",
    /// which lists a token by name/prefix/lifecycle so a person can see what credentials exist without the
    /// secret. Newest first, client-side ordered (SQLite can't ORDER BY a DateTimeOffset).</summary>
    public async Task<List<ApiTokenMetadata>> ListMetadataAsync(string householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ApiTokens.AsNoTracking()
            .Where(t => t.HouseholdId == householdId)
            .Select(t => new ApiTokenMetadata(t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt, t.RevokedAt, t.ExpiresAt))
            .ToListAsync(ct);
        return [.. rows.OrderByDescending(t => t.CreatedAt)];
    }

    /// <summary>How many tokens this household has — for the "N records will be removed" delete confirm.</summary>
    public async Task<int> CountForHouseholdAsync(string householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiTokens.Where(t => t.HouseholdId == householdId).CountAsync(ct);
    }

    /// <summary>Remove ALL of a household's tokens — the "delete my data" wiring. A token is a credential
    /// the household created, so it's theirs to have wiped; the household id scopes it (auth.db has no
    /// query filter). Returns how many were removed.</summary>
    public async Task<int> DeleteAllForHouseholdAsync(string householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiTokens.Where(t => t.HouseholdId == householdId).ExecuteDeleteAsync(ct);
    }

    /// <summary>SHA-256 (hex) of the raw secret — the same shape <c>ErrorLogStore.FingerprintOf</c> uses.
    /// Deterministic, so the handler hashes a presented secret and matches it against the stored hash.</summary>
    private static string HashOf(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
