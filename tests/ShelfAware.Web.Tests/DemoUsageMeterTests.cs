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

    private sealed class CapturingLogger : ILogger<DemoUsageMeter>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
