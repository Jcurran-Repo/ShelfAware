using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>Mint / validate / list / revoke for GraphQL API tokens, over a real in-memory auth.db.
/// The load-bearing claims: the raw secret is never stored (only its hash + a display prefix), a
/// presented secret round-trips through the hash to its token, and revoked / expired / foreign tokens
/// are all refused.</summary>
public class ApiTokenServiceTests : IDisposable
{
    private readonly TestAuthDb _authDb = new();
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.FromHours(-5));

    public void Dispose() => _authDb.Dispose();

    private ApiTokenService Service() => new(_authDb);

    private static string ExpectedHash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    [Fact]
    public async Task Minting_stores_only_the_hash_and_a_display_prefix_never_the_secret()
    {
        var mint = await Service().CreateAsync("hh-a", "user-1", "meal planner", Now);

        Assert.StartsWith("sa_", mint.Secret);
        Assert.True(mint.Secret.Length > 11, "the secret is the prefix plus real entropy");

        await using var db = _authDb.CreateDbContext();
        var stored = await db.ApiTokens.AsNoTracking().SingleAsync();

        // The secret itself is nowhere in the row — only its SHA-256 (hex) and a non-secret display slice.
        Assert.Equal(ExpectedHash(mint.Secret), stored.TokenHash);
        Assert.NotEqual(mint.Secret, stored.TokenHash);
        Assert.Equal(mint.Secret[..11], stored.Prefix);
        Assert.Equal("meal planner", stored.Name);
        Assert.Equal("hh-a", stored.HouseholdId);
        Assert.Equal("user-1", stored.CreatedByUserId);
        Assert.Equal(Now, stored.CreatedAt);
    }

    [Fact]
    public async Task A_minted_token_validates_by_its_secret_and_the_stamp_lands()
    {
        var mint = await Service().CreateAsync("hh-a", "user-1", "script", Now);

        var resolved = await Service().ValidateAsync(mint.Secret, Now.AddHours(1));

        Assert.NotNull(resolved);
        Assert.Equal("hh-a", resolved!.HouseholdId);
        Assert.Equal(Now.AddHours(1), resolved.LastUsedAt); // stamped with the clock the caller named

        // The stamp persisted, not just on the returned instance.
        await using var db = _authDb.CreateDbContext();
        Assert.Equal(Now.AddHours(1), (await db.ApiTokens.AsNoTracking().SingleAsync()).LastUsedAt);
    }

    [Fact]
    public async Task A_wrong_or_blank_secret_resolves_to_nothing()
    {
        await Service().CreateAsync("hh-a", "user-1", "script", Now);

        Assert.Null(await Service().ValidateAsync("sa_not-the-real-secret", Now));
        Assert.Null(await Service().ValidateAsync("", Now));
        Assert.Null(await Service().ValidateAsync(null, Now));
        Assert.Null(await Service().ValidateAsync("   ", Now));
    }

    [Fact]
    public async Task A_revoked_token_is_refused()
    {
        var mint = await Service().CreateAsync("hh-a", "user-1", "script", Now);
        var id = mint.Token.Id;

        Assert.True(await Service().RevokeAsync(id, "hh-a", Now));
        Assert.Null(await Service().ValidateAsync(mint.Secret, Now.AddHours(1)));
    }

    [Fact]
    public async Task Revoking_is_conditional_so_a_second_revoke_reports_nothing_changed()
    {
        var mint = await Service().CreateAsync("hh-a", "user-1", "script", Now);

        Assert.True(await Service().RevokeAsync(mint.Token.Id, "hh-a", Now));
        Assert.False(await Service().RevokeAsync(mint.Token.Id, "hh-a", Now.AddHours(1)));

        // The original revoke stamp is untouched — the no-op second call didn't re-stamp it.
        await using var db = _authDb.CreateDbContext();
        Assert.Equal(Now, (await db.ApiTokens.AsNoTracking().SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task A_household_cannot_revoke_another_households_token()
    {
        var mint = await Service().CreateAsync("hh-a", "user-1", "script", Now);

        // hh-b names hh-a's token id — the WHERE's household scope is the tenancy guard (auth.db has no
        // query filter), so this must miss the row entirely, not revoke it.
        Assert.False(await Service().RevokeAsync(mint.Token.Id, "hh-b", Now));
        Assert.NotNull(await Service().ValidateAsync(mint.Secret, Now.AddHours(1)));
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_a_still_live_one_is_accepted()
    {
        var expiring = await Service().CreateAsync("hh-a", "user-1", "short", Now, expiresAt: Now.AddDays(1));

        Assert.NotNull(await Service().ValidateAsync(expiring.Secret, Now));            // before expiry
        Assert.NotNull(await Service().ValidateAsync(expiring.Secret, Now.AddHours(23))); // still before
        Assert.Null(await Service().ValidateAsync(expiring.Secret, Now.AddDays(2)));     // past expiry

        // The boundary: at-expiry counts as expired (> not >=), matching Household.InviteIsUsable.
        Assert.Null(await Service().ValidateAsync(expiring.Secret, Now.AddDays(1)));
    }

    [Fact]
    public async Task Two_mints_produce_distinct_secrets_and_hashes()
    {
        var a = await Service().CreateAsync("hh-a", "user-1", "one", Now);
        var b = await Service().CreateAsync("hh-a", "user-1", "two", Now);

        Assert.NotEqual(a.Secret, b.Secret);
        Assert.NotEqual(a.Token.TokenHash, b.Token.TokenHash);
    }

    [Fact]
    public async Task Listing_returns_only_this_households_tokens_newest_first()
    {
        var svc = Service();
        var first = await svc.CreateAsync("hh-a", "user-1", "first", Now);
        var second = await svc.CreateAsync("hh-a", "user-1", "second", Now.AddMinutes(5));
        await svc.CreateAsync("hh-b", "user-9", "other households", Now);

        var listed = await svc.ListAsync("hh-a");

        Assert.Equal(2, listed.Count);
        Assert.Equal(second.Token.Id, listed[0].Id); // newest first
        Assert.Equal(first.Token.Id, listed[1].Id);
        Assert.DoesNotContain(listed, t => t.HouseholdId == "hh-b");
    }

    [Fact]
    public async Task ListMetadata_returns_export_safe_rows_scoped_to_the_household_newest_first()
    {
        var svc = Service();
        await svc.CreateAsync("hh-a", "user-1", "first", Now);
        var second = await svc.CreateAsync("hh-a", "user-1", "second", Now.AddMinutes(5));
        await svc.CreateAsync("hh-b", "user-9", "other household", Now);

        var metadata = await svc.ListMetadataAsync("hh-a");

        Assert.Equal(2, metadata.Count);
        Assert.Equal("second", metadata[0].Name); // newest first
        Assert.Equal(second.Token.Prefix, metadata[0].Prefix);
        Assert.DoesNotContain(metadata, m => m.Name == "other household"); // household-scoped
        // The metadata record has no hash field at all — the credential can't leak through an export. This
        // asserts the shape the export serializes (name/prefix/dates), which is exactly the safe surface.
        Assert.All(metadata, m => Assert.StartsWith("sa_", m.Prefix));
    }

    [Fact]
    public async Task Count_and_DeleteAll_are_household_scoped()
    {
        var svc = Service();
        await svc.CreateAsync("hh-a", "user-1", "one", Now);
        await svc.CreateAsync("hh-a", "user-1", "two", Now);
        await svc.CreateAsync("hh-b", "user-9", "theirs", Now);

        Assert.Equal(2, await svc.CountForHouseholdAsync("hh-a"));

        Assert.Equal(2, await svc.DeleteAllForHouseholdAsync("hh-a"));

        Assert.Equal(0, await svc.CountForHouseholdAsync("hh-a"));
        Assert.Equal(1, await svc.CountForHouseholdAsync("hh-b")); // the other household is untouched
    }

    // (revoked, expiresAt-offset-hours-from-Now, expected-usable) — the ONE reading of "is this
    // credential live", mirroring the Household.InviteIsUsable / ErrorLogEntry.Resolved theory style.
    public static TheoryData<bool, int?, bool> UsableCases => new()
    {
        { false, null, true },   // live, no expiry
        { false, 1, true },      // live, expires in an hour
        { false, -1, false },    // expired an hour ago
        { false, 0, false },     // at the expiry instant — expired (> not >=)
        { true, null, false },   // revoked beats everything
        { true, 1, false },      // revoked even though not yet expired
    };

    [Theory]
    [MemberData(nameof(UsableCases))]
    public void IsUsable_is_the_one_reading_of_live(bool revoked, int? expiryOffsetHours, bool expected)
    {
        var token = new ApiToken
        {
            HouseholdId = "hh-a", CreatedByUserId = "u", Name = "n", TokenHash = "h", Prefix = "sa_x",
            RevokedAt = revoked ? Now.AddHours(-2) : null,
            ExpiresAt = expiryOffsetHours is { } h ? Now.AddHours(h) : null,
        };

        Assert.Equal(expected, token.IsUsable(Now));
    }
}
