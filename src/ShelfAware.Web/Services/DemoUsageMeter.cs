using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Services;

/// <summary>The come-back-tomorrow pre-check for a UI surface, split out from the concrete DB-backed
/// <see cref="DemoUsageMeter"/> so <see cref="AiErrorText"/> can ask "is the whole box capped for today?"
/// without dragging a database into its tests. Returns null unless a Demo cap is configured AND today's
/// box-wide count has reached it — a total no-op on a family / self-host box.</summary>
public interface IDemoValve
{
    ValueTask<string?> CallBlockedMessageAsync(CancellationToken cancellationToken = default);
}

/// <summary>The managed demo box's BOX-WIDE daily AI valve — the wallet bound a public box with open
/// registration needs that the per-household <see cref="AiUsageMeter"/> can't give (every new household
/// gets its own daily allowance). Counts host-key LLM calls per day in ONE row (<see cref="DemoUsageDay"/>,
/// auth.db operator data, like the error log), enforces the configured global cap, and warns the admin once
/// the day crosses the alert threshold.
/// <para>A NO-OP when nothing is configured (all <see cref="DemoOptions"/> null — the family / self-host
/// default): it enforces nothing and writes no row, so those boxes are untouched. Only counts host-key
/// (managed) calls — a BYOK visitor rides their own wallet and never touches this counter.</para>
/// <para>TTS is deliberately NOT metered here: the managed demo box reads recipes with a free self-hosted
/// Kokoro sidecar (Speech:Provider=Local), so there's no per-synthesis cost to bound.</para></summary>
public sealed class DemoUsageMeter(
    IDbContextFactory<AuthDbContext> dbFactory,
    IOptions<DemoOptions> options,
    ILogger<DemoUsageMeter> logger) : IDemoValve
{
    private DemoOptions Opt => options.Value;

    /// <summary>Any box-wide cap OR the alert is configured. When false every method is a no-op and no row
    /// is ever written — the family / self-host posture. Public so /admin can decide whether to show the
    /// usage panel at all (a box with no Demo config has nothing to show).</summary>
    public bool IsConfigured => Opt.DailyGlobalCallLimit is not null || Opt.AlertThreshold is not null;

    /// <summary>Throw (with the polite come-back message) when today's box-wide LLM calls have hit the cap.
    /// Call BEFORE the provider call. The <see cref="MeteredChatClient"/> gate uses this; the surfaces use
    /// the non-throwing <see cref="CallBlockedMessageAsync"/> twin, which shares the same block check so the
    /// two can never disagree about why a call is refused.</summary>
    public async Task EnsureCallAllowedAsync(CancellationToken ct = default)
    {
        if (await IsCallBlockedAsync(ct)) throw new DemoDailyCapException();
    }

    /// <inheritdoc />
    public async ValueTask<string?> CallBlockedMessageAsync(CancellationToken ct = default) =>
        await IsCallBlockedAsync(ct) ? DemoLimits.DailyCapReachedMessage : null;

    /// <summary>THE one reading of "is the box-wide LLM cap hit right now?" — shared by the throwing gate
    /// and the non-throwing pre-check so a surface and the server-side gate never disagree. False (with no
    /// DB read) when no cap is configured.</summary>
    private async Task<bool> IsCallBlockedAsync(CancellationToken ct)
    {
        if (Opt.DailyGlobalCallLimit is not int cap) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await TodayAsync(db, ct))?.Calls >= cap;
    }

    public Task RecordCallAsync(CancellationToken ct = default) => AccumulateAsync(calls: 1, ct);

    /// <summary>Today's box-wide counter (for /admin), or null if nothing is configured or recorded yet.</summary>
    public async Task<DemoUsageDay?> GetTodayAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await TodayAsync(db, ct);
    }

    private async Task AccumulateAsync(int calls, CancellationToken ct)
    {
        if (!IsConfigured) return; // family / self-host: never write a row

        var today = DateOnly.FromDateTime(DateTime.Today);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Upsert on the day row, race-safe without a transaction (the AiUsageMeter pattern): increment in
        // place, and only insert when there's no row yet — a concurrent insert that beats us (unique-index
        // collision) means we add onto theirs instead.
        if (await IncrementAsync(db, today, calls, ct) == 0)
        {
            db.DemoUsage.Add(new DemoUsageDay { Day = today, Calls = calls });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
            {
                db.ChangeTracker.Clear();
                await IncrementAsync(db, today, calls, ct);
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
                    "Demo box: today's host-key AI calls crossed the alert threshold ({Threshold}). Watch usage on /admin.",
                    threshold);
            }
        }
    }

    private static Task<int> IncrementAsync(AuthDbContext db, DateOnly today, int calls, CancellationToken ct)
        => db.DemoUsage.Where(d => d.Day == today).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Calls, d => d.Calls + calls),
            ct);

    private static Task<DemoUsageDay?> TodayAsync(AuthDbContext db, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return db.DemoUsage.AsNoTracking().FirstOrDefaultAsync(d => d.Day == today, ct);
    }
}
