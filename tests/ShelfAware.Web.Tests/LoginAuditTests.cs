using Microsoft.Data.Sqlite;
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

    [Fact]
    public async Task A_first_login_that_loses_the_insert_race_falls_back_to_the_increment_not_a_second_row()
    {
        // Two devices sign in for the FIRST time at the same instant: RecordAsync's IncrementAsync sees no
        // row, so it inserts — but the other sign-in slips the same PK in first, so this insert hits the
        // constraint. The catch(DbUpdateException) fallback must increment the winner's row, NOT throw and
        // NOT mint a duplicate. RacingAuthDbFactory reproduces exactly that race deterministically (the
        // "concurrent" writer commits from its own context inside this one's SaveChanges).
        using var racing = new RacingAuthDbFactory();
        var audit = new LoginAudit(racing, NullLogger<LoginAudit>.Instance);

        await audit.RecordAsync("u1", "jordan@example.com", T0);

        await using var db = racing.CreateDbContext();
        var row = Assert.Single(await db.UserLoginStats.ToListAsync()); // one row — the PK held
        Assert.Equal("u1", row.UserId);
        Assert.Equal(2, row.LoginCount);               // the racer's insert (1) + this login's fallback increment (1)
        Assert.Equal("jordan@example.com", row.Email); // the fallback refreshed Email to this caller — proof it ran
    }

    /// <summary>A real in-memory auth.db (like <see cref="TestAuthDb"/>) whose FIRST handed-out context is
    /// a <see cref="RacingAuthDbContext"/> — it lets a concurrent writer insert the same login-stat row
    /// mid-save so the caller's insert loses the PK race. Every later context is plain, so a read after
    /// the race is ordinary.</summary>
    private sealed class RacingAuthDbFactory : IDbContextFactory<AuthDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AuthDbContext> _options;
        private bool _armed = true;

        public RacingAuthDbFactory()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_connection).Options;
            using var db = new AuthDbContext(_options);
            db.Database.EnsureCreated();
        }

        public AuthDbContext CreateDbContext()
        {
            if (_armed)
            {
                _armed = false; // only the first context (RecordAsync's) races; the test's read is plain
                return new RacingAuthDbContext(_options, () => new AuthDbContext(_options));
            }
            return new AuthDbContext(_options);
        }

        public Task<AuthDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        public void Dispose() => _connection.Dispose();
    }

    /// <summary>The first time this context tries to INSERT login-stat rows, a "concurrent" sign-in inserts
    /// the same account first (its own context, committed before we save), so our insert below loses the PK
    /// race — the two-devices-at-once case RecordAsync's DbUpdateException catch exists for. One-shot, so
    /// the fallback's own write (an ExecuteUpdate, which doesn't route here anyway) is never intercepted.</summary>
    private sealed class RacingAuthDbContext(DbContextOptions<AuthDbContext> options, Func<AuthDbContext> concurrentWriter)
        : AuthDbContext(options)
    {
        private bool _raced;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_raced)
            {
                var incoming = ChangeTracker.Entries<UserLoginStat>()
                    .Where(e => e.State == EntityState.Added)
                    .Select(e => e.Entity)
                    .ToList();
                if (incoming.Count > 0)
                {
                    _raced = true;
                    await using var other = concurrentWriter();
                    foreach (var e in incoming)
                        other.UserLoginStats.Add(new UserLoginStat
                        {
                            UserId = e.UserId,
                            Email = "concurrent@example.com",
                            LoginCount = 1,
                            FirstLoginAt = e.FirstLoginAt,
                            LastLoginAt = e.FirstLoginAt,
                        });
                    await other.SaveChangesAsync(cancellationToken);
                }
            }
            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
