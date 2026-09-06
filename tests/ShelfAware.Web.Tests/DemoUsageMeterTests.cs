using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The managed demo box's BOX-WIDE daily AI valve — the wallet bound that per-household caps can't give
/// under open registration. Real in-memory SQLite auth.db so the day-row upsert + unique index behave as
/// production. The family / self-host posture (no Demo config) must stay a total no-op: no enforcement,
/// no rows written.
/// </summary>
public sealed class DemoUsageMeterTests : IDisposable
{
    private readonly TestAuthDb _authDb = new();

    public void Dispose() => _authDb.Dispose();

    private DemoUsageMeter Meter(DemoOptions options, ILogger<DemoUsageMeter>? logger = null) =>
        new(_authDb, Options.Create(options), logger ?? NullLogger<DemoUsageMeter>.Instance);

    [Fact]
    public async Task Unconfigured_it_enforces_nothing_and_writes_no_row()
    {
        var meter = Meter(new DemoOptions()); // all null — the family / self-host default

        Assert.False(meter.IsConfigured);
        await meter.EnsureCallAllowedAsync();               // no cap → never throws
        Assert.Null(await meter.CallBlockedMessageAsync()); // and the pre-check never blocks
        await meter.RecordCallAsync();

        Assert.Null(await meter.GetTodayAsync());          // no row surfaced
        await using var db = _authDb.CreateDbContext();
        Assert.Empty(await db.DemoUsage.ToListAsync());     // and none physically written
    }

    [Fact]
    public async Task It_records_calls_into_todays_row()
    {
        var meter = Meter(new DemoOptions { DailyGlobalCallLimit = 10 });

        await meter.RecordCallAsync();
        await meter.RecordCallAsync();

        var today = await meter.GetTodayAsync();
        Assert.NotNull(today);
        Assert.Equal(2, today!.Calls);
    }

    [Fact]
    public async Task It_enforces_the_box_wide_daily_call_cap()
    {
        var meter = Meter(new DemoOptions { DailyGlobalCallLimit = 3 });

        for (var i = 0; i < 3; i++)
        {
            await meter.EnsureCallAllowedAsync(); // under the cap — allowed
            await meter.RecordCallAsync();
        }

        // The 4th is refused with the polite come-back message.
        var ex = await Assert.ThrowsAsync<DemoDailyCapException>(() => meter.EnsureCallAllowedAsync());
        Assert.Contains("come back tomorrow", ex.Message);
    }

    // 0 is an emergency kill switch — it must block the FIRST call, not admit one before a row exists
    // (the null >= 0 hole). Both the gate and the pre-check agree.
    [Fact]
    public async Task A_cap_of_zero_blocks_the_very_first_call()
    {
        var meter = Meter(new DemoOptions { DailyGlobalCallLimit = 0 });

        await Assert.ThrowsAsync<DemoDailyCapException>(() => meter.EnsureCallAllowedAsync());
        Assert.NotNull(await meter.CallBlockedMessageAsync());
    }

    // The non-throwing pre-check twin the UI surfaces use (via AiErrorText): it reports the come-back message
    // at exactly the point EnsureCallAllowedAsync would throw, so a surface skips a doomed call and shows the
    // right words instead of a generic failure — and stays null under the cap.
    [Fact]
    public async Task The_pre_check_reports_the_come_back_message_at_the_cap()
    {
        var meter = Meter(new DemoOptions { DailyGlobalCallLimit = 2 });

        Assert.Null(await meter.CallBlockedMessageAsync()); // under the cap
        await meter.RecordCallAsync();
        Assert.Null(await meter.CallBlockedMessageAsync());
        await meter.RecordCallAsync();

        var message = await meter.CallBlockedMessageAsync(); // at the cap
        Assert.NotNull(message);
        Assert.Contains("come back tomorrow", message);
    }

    [Fact]
    public async Task It_alerts_the_admin_once_when_the_day_crosses_the_threshold()
    {
        var log = new CapturingLogger();
        var meter = Meter(new DemoOptions { AlertThreshold = 2 }, log);

        await meter.RecordCallAsync(); // count 1 — under
        Assert.Empty(log.Warnings);
        await meter.RecordCallAsync(); // count 2 — crosses → one alert
        await meter.RecordCallAsync(); // count 3 — past, must NOT alert again

        var warning = Assert.Single(log.Warnings);
        Assert.Contains("alert threshold", warning);
    }

    [Fact]
    public async Task A_read_failure_fails_open_instead_of_crashing_the_surface()
    {
        // MED: IsCallBlockedAsync feeds the pre-check that runs BEFORE each AI surface's own try, so a
        // transient auth.db read failure must NOT throw (that tears down the circuit) — it fails open (not
        // blocked), the key's own spend limit being the backstop. Same posture as AiUsageMeter's gate read.
        var log = new CapturingLogger();
        var meter = new DemoUsageMeter(
            new ThrowingAuthDbFactory(), Options.Create(new DemoOptions { DailyGlobalCallLimit = 5 }), log);

        Assert.Null(await meter.CallBlockedMessageAsync()); // no throw, and not blocked
        await meter.EnsureCallAllowedAsync();               // the gate doesn't throw either

        Assert.NotEmpty(log.Errors); // the failure was logged, not silently swallowed
    }

    [Fact]
    public async Task A_release_with_no_row_for_today_does_not_write_a_negative_row()
    {
        // LOW: a release straddling midnight lands on a fresh day with no row and has nothing to give back,
        // so it must NOT insert a "-1 calls" row (which would raise the effective cap and read "-1" on /admin).
        var meter = Meter(new DemoOptions { DailyGlobalCallLimit = 5 });

        await meter.ReleaseCallAsync(); // no reserve today — nothing to release

        Assert.Null(await meter.GetTodayAsync()); // no row written (a -1 row before the guard)
    }

    [Fact]
    public async Task Two_first_of_day_writes_racing_to_insert_dont_lose_a_count()
    {
        // The upsert is race-safe: if two requests both find no row for today and both try to INSERT, the
        // loser hits the unique index (SQLITE_CONSTRAINT_UNIQUE 2067) and must fall back to the retry-
        // increment, adding onto the winner's row rather than throwing and losing its count. Forced
        // deterministically: an interceptor inserts today's row (the "winner") from a second context the
        // instant before the meter's own insert reaches the DB. Mutating the caught error codes makes the
        // collision propagate → RecordCallAsync throws → this test fails.
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var interceptor = new RaceInsertInterceptor(async ct =>
        {
            // A SEPARATE, non-intercepted context on the same DB inserts the winning row (Calls = 5).
            var plain = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(conn).Options;
            await using var other = new AuthDbContext(plain);
            other.DemoUsage.Add(new DemoUsageDay { Day = today, Calls = 5 });
            await other.SaveChangesAsync(ct);
        });
        var schemaOptions = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(conn).Options;
        using (var schema = new AuthDbContext(schemaOptions)) schema.Database.EnsureCreated();
        var meterOptions = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(conn).AddInterceptors(interceptor).Options;

        var meter = new DemoUsageMeter(
            new OptionsAuthDbFactory(meterOptions),
            Options.Create(new DemoOptions { DailyGlobalCallLimit = 100 }), NullLogger<DemoUsageMeter>.Instance);

        await meter.RecordCallAsync(); // our +1 collides with the winner's 5 → falls back → 6, no throw

        using var read = new AuthDbContext(schemaOptions);
        var row = await read.DemoUsage.FirstOrDefaultAsync(d => d.Day == today);
        Assert.Equal(6, row?.Calls); // both deltas survived: winner 5 + our 1
    }

    private sealed class ThrowingAuthDbFactory : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => throw new InvalidOperationException("auth.db unavailable");
        public Task<AuthDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("auth.db unavailable");
    }

    private sealed class OptionsAuthDbFactory(DbContextOptions<AuthDbContext> options) : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => new(options);
        public Task<AuthDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class CapturingLogger : ILogger<DemoUsageMeter>
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            else if (logLevel == LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }
}
