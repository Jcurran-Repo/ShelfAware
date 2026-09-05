using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Services;

/// <summary>The managed demo box's BOX-WIDE daily AI valve — the wallet bound a public box with open
/// registration needs that the per-household <see cref="AiUsageMeter"/> can't give (every new household
/// gets its own daily allowance). Counts host-key LLM calls + read-aloud TTS syntheses per day in ONE row
/// (<see cref="DemoUsageDay"/>, auth.db operator data, like the error log), enforces the configured global
/// caps, and warns the admin once the day crosses the alert threshold.
/// <para>A NO-OP when nothing is configured (all <see cref="DemoOptions"/> null — the family / self-host
/// default): it enforces nothing and writes no row, so those boxes are untouched. Only counts host-key
/// (managed) calls — a BYOK visitor rides their own wallet and never touches this counter.</para></summary>
public sealed class DemoUsageMeter(
    IDbContextFactory<AuthDbContext> dbFactory,
    IOptions<DemoOptions> options,
    ILogger<DemoUsageMeter> logger)
{
    private DemoOptions Opt => options.Value;

    /// <summary>Any box-wide bound OR the alert is configured. When false every method is a no-op and no row
    /// is ever written — the family / self-host posture.</summary>
    private bool Active =>
        Opt.DailyGlobalCallLimit is not null || Opt.DailyGlobalVoiceLimit is not null || Opt.AlertThreshold is not null;

    /// <summary>Throw (with the polite come-back message) when today's box-wide LLM calls have hit the cap.
    /// Call BEFORE the provider call.</summary>
    public async Task EnsureCallAllowedAsync(CancellationToken ct = default)
    {
        if (Opt.DailyGlobalCallLimit is not int cap) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if ((await TodayAsync(db, ct))?.Calls >= cap) throw new DemoDailyCapException();
    }

    /// <summary>Throw when today's box-wide read-aloud TTS syntheses have hit the cap. Call before a
    /// cache-MISS synthesis (a cache hit is free and must not be gated).</summary>
    public async Task EnsureVoiceAllowedAsync(CancellationToken ct = default)
    {
        if (Opt.DailyGlobalVoiceLimit is not int cap) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if ((await TodayAsync(db, ct))?.VoiceCalls >= cap) throw new DemoDailyCapException();
    }

    public Task RecordCallAsync(CancellationToken ct = default) => AccumulateAsync(calls: 1, voice: 0, ct);
    public Task RecordVoiceAsync(CancellationToken ct = default) => AccumulateAsync(calls: 0, voice: 1, ct);

    /// <summary>Today's box-wide counters (for /admin), or null if nothing is configured or recorded yet.</summary>
    public async Task<DemoUsageDay?> GetTodayAsync(CancellationToken ct = default)
    {
        if (!Active) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await TodayAsync(db, ct);
    }

    private async Task AccumulateAsync(int calls, int voice, CancellationToken ct)
    {
        if (!Active) return; // family / self-host: never write a row

        var today = DateOnly.FromDateTime(DateTime.Today);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Upsert on the day row, race-safe without a transaction (the AiUsageMeter pattern): increment in
        // place, and only insert when there's no row yet — a concurrent insert that beats us (unique-index
        // collision) means we add onto theirs instead.
        if (await IncrementAsync(db, today, calls, voice, ct) == 0)
        {
            db.DemoUsage.Add(new DemoUsageDay { Day = today, Calls = calls, VoiceCalls = voice });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
            {
                db.ChangeTracker.Clear();
                await IncrementAsync(db, today, calls, voice, ct);
            }
        }

        // Alert on the LLM call that crosses the threshold — the "traffic is arriving" signal. Best-effort
        // (a concurrent burst can step past the exact value and miss it), and at most once, so it can't spam
        // the log; the hard cap is the real bound. Re-read in a fresh context so it reflects the write above.
        if (calls > 0 && Opt.AlertThreshold is int threshold)
        {
            await using var check = await dbFactory.CreateDbContextAsync(ct);
            if ((await TodayAsync(check, ct))?.Calls == threshold)
            {
                logger.LogWarning(
                    "Demo box: {Calls} host-key AI calls used today — crossed the alert threshold ({Threshold}). Watch usage on /admin.",
                    threshold, threshold);
            }
        }
    }

    private static Task<int> IncrementAsync(AuthDbContext db, DateOnly today, int calls, int voice, CancellationToken ct)
        => db.DemoUsage.Where(d => d.Day == today).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Calls, d => d.Calls + calls)
                .SetProperty(d => d.VoiceCalls, d => d.VoiceCalls + voice),
            ct);

    private static Task<DemoUsageDay?> TodayAsync(AuthDbContext db, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return db.DemoUsage.AsNoTracking().FirstOrDefaultAsync(d => d.Day == today, ct);
    }
}
