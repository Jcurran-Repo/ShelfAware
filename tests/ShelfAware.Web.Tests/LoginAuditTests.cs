using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>The persisted login count over a real in-memory auth.db. The load-bearing claims: the first
/// login inserts one row, every later login for the SAME account increments that row (never a second),
/// FirstLoginAt is preserved while LastLoginAt/Email advance, and the list is most-recently-active first.</summary>
public class LoginAuditTests : IDisposable
{
    private readonly TestAuthDb _authDb = new();
    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 9, 0, 0, TimeSpan.FromHours(-5));

    public void Dispose() => _authDb.Dispose();

    private LoginAudit Audit() => new(_authDb, NullLogger<LoginAudit>.Instance);

    private async Task<UserLoginStat?> RowAsync(string userId)
    {
        await using var db = _authDb.CreateDbContext();
        return await db.UserLoginStats.AsNoTracking().SingleOrDefaultAsync(s => s.UserId == userId);
    }

    [Fact]
    public async Task A_first_login_inserts_one_row_with_count_one()
    {
        await Audit().RecordAsync("u1", "jordan@example.com", T0);

        var row = await RowAsync("u1");
        Assert.NotNull(row);
        Assert.Equal(1, row!.LoginCount);
        Assert.Equal("jordan@example.com", row.Email);
        Assert.Equal(T0, row.FirstLoginAt);
        Assert.Equal(T0, row.LastLoginAt);
    }

    [Fact]
    public async Task A_second_login_increments_the_same_row_and_advances_last_but_keeps_first()
    {
        var audit = Audit();
        await audit.RecordAsync("u1", "jordan@example.com", T0);
        await audit.RecordAsync("u1", "jordan@example.com", T0.AddDays(3));

        // ONE row, not two — the upsert keys on the user id.
        await using (var db = _authDb.CreateDbContext())
            Assert.Single(await db.UserLoginStats.ToListAsync());

        var row = await RowAsync("u1");
        Assert.Equal(2, row!.LoginCount);
        Assert.Equal(T0, row.FirstLoginAt);          // first arrival preserved
        Assert.Equal(T0.AddDays(3), row.LastLoginAt); // last advanced
    }

    [Fact]
    public async Task A_changed_email_is_refreshed_on_the_next_login()
    {
        var audit = Audit();
        await audit.RecordAsync("u1", "old@example.com", T0);
        await audit.RecordAsync("u1", "new@example.com", T0.AddDays(1));

        Assert.Equal("new@example.com", (await RowAsync("u1"))!.Email);
    }

    [Fact]
    public async Task Different_accounts_get_different_rows()
    {
        var audit = Audit();
        await audit.RecordAsync("u1", "a@example.com", T0);
        await audit.RecordAsync("u2", "b@example.com", T0);

        await using var db = _authDb.CreateDbContext();
        Assert.Equal(2, await db.UserLoginStats.CountAsync());
    }

    [Fact]
    public async Task The_list_is_most_recently_active_first()
    {
        var audit = Audit();
        await audit.RecordAsync("u1", "older@example.com", T0);
        await audit.RecordAsync("u2", "newer@example.com", T0.AddHours(5));

        var stats = await audit.ListAsync();

        Assert.Equal(["newer@example.com", "older@example.com"], stats.Select(s => s.Email));
    }
}
