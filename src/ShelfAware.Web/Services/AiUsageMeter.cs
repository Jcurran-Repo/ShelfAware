using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>
/// Per-household daily AI quotas on a MANAGED-key deployment — the "one visitor can't drain the
/// host's wallet" guard that had to exist before managed keys go on a public box. Counts LLM calls,
/// tokens, and cook-along session mints in one row per (household, day); limits come from config and
/// default to unlimited (self-host). This records what any future billing would need — pricing itself
/// stays a separate workstream. Scoped: rides the same household resolution as all data access.
/// </summary>
public sealed class AiUsageMeter(
    IHouseholdDbFactory dbFactory,
    IOptions<LlmOptions> llm,
    IOptions<ElevenLabsOptions> elevenLabs,
    IEntitlements entitlements,
    ILogger<AiUsageMeter> logger)
{
    public sealed record TodayUsage(int Calls, long Tokens, int VoiceSessionMints, long CostMicros);

    public sealed record DayUsage(
        DateOnly Day, int Calls, long InputTokens, long OutputTokens, int VoiceSessionMints, long CostMicros)
    {
        public long Tokens => InputTokens + OutputTokens;
    }

    /// <summary>A calendar month's rolled-up usage — the "is it steady month to month?" view the operator
    /// needs before freezing any pricing. <see cref="CostMicros"/> is the month's total LLM cost.</summary>
    public sealed record MonthUsage(int Year, int Month, int Calls, long Tokens, long CostMicros);

    public async Task<TodayUsage> GetTodayAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await TodayRowAsync(db, cancellationToken);
        return row is null
            ? new TodayUsage(0, 0, 0, 0)
            : new TodayUsage(row.Calls, row.InputTokens + row.OutputTokens, row.VoiceSessionMints, row.CostMicros);
    }

    /// <summary>The household's most recent usage rows, newest first — the Settings usage panel.
    /// Days with no AI activity have no row, so gaps are normal.</summary>
    public async Task<IReadOnlyList<DayUsage>> GetRecentAsync(int days, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AiUsages.AsNoTracking()
            .OrderByDescending(u => u.Day)
            .Take(days)
            .Select(u => new DayUsage(u.Day, u.Calls, u.InputTokens, u.OutputTokens, u.VoiceSessionMints, u.CostMicros))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The last <paramref name="months"/> calendar months of usage, newest first — calls, tokens,
    /// and total cost per month. The operator's "is it consistent month to month?" view (Settings), so the
    /// pricing/limit values can be set against a real trend rather than a guess. Months with no AI activity
    /// are omitted (a gap, not a zero row). Grouped in memory — usage is one row per active day, a small set.</summary>
    public async Task<IReadOnlyList<MonthUsage>> GetMonthlyAsync(int months, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AiUsages.AsNoTracking()
            .Where(u => u.Day >= firstMonth)
            .Select(u => new { u.Day, u.Calls, u.InputTokens, u.OutputTokens, u.CostMicros })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => new { r.Day.Year, r.Day.Month })
            .Select(g => new MonthUsage(
                g.Key.Year, g.Key.Month,
                g.Sum(r => r.Calls),
                g.Sum(r => r.InputTokens + r.OutputTokens),
                g.Sum(r => r.CostMicros)))
            .OrderByDescending(m => m.Year).ThenByDescending(m => m.Month)
            .ToList();
    }

    /// <summary>Throws (with user-presentable text — the AI surfaces show exception-adjacent friendly
    /// errors) when today's LLM usage has reached a configured cap. Call BEFORE the provider call.</summary>
    public async Task EnsureLlmCallAllowedAsync(CancellationToken cancellationToken = default)
    {
        if (llm.Value.DailyCallLimit is null && llm.Value.DailyTokenLimit is null) return;

        // A Founder household is exempt from the caps entirely (unlimited-but-recorded). Consulted AFTER
        // the no-limit check above, so a deployment that configures no caps never pays the tier read;
        // and only the GATE is skipped — the reserve/record after the provider replies are untouched, so a
        // Founder's usage still lands in the row.
        if ((await entitlements.GetTierAsync(cancellationToken)).IsUnlimited()) return;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await TodayRowAsync(db, cancellationToken);
        // Coalesce the absent row to zero rather than early-returning: a limit of 0 must block the FIRST
        // call, not admit one before a row exists (0 >= 0). A no-row day with a positive limit still passes.
        var calls = row?.Calls ?? 0;
        var tokens = (row?.InputTokens ?? 0) + (row?.OutputTokens ?? 0);

        if (llm.Value.DailyCallLimit is int callLimit && calls >= callLimit)
        {
            throw new InvalidOperationException(
                "Today's AI allowance on this server is used up — it resets tomorrow. " +
                "(Bringing your own key in Settings is never limited.)");
        }
        if (llm.Value.DailyTokenLimit is long tokenLimit && tokens >= tokenLimit)
        {
            throw new InvalidOperationException(
                "Today's AI allowance on this server is used up — it resets tomorrow. " +
                "(Bringing your own key in Settings is never limited.)");
        }
    }

    /// <summary>True when this household may mint another cook-along session today.</summary>
    public async Task<bool> MayMintVoiceSessionAsync(CancellationToken cancellationToken = default)
    {
        if (elevenLabs.Value.DailySignedUrlLimit is not int limit) return true;
        // Founder is exempt from the voice cap too; the endpoint still records the mint afterward.
        if ((await entitlements.GetTierAsync(cancellationToken)).IsUnlimited()) return true;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await TodayRowAsync(db, cancellationToken);
        return row is null || row.VoiceSessionMints < limit;
    }

    /// <summary>Count one call against today's row, BEFORE the provider call — split from the token/cost
    /// record so <see cref="MeteredChatClient"/> can reserve the call uncancellably at the gate. Counting
    /// the call at the gate is what bounds the daily CALL cap even when a client aborts mid-flight (an
    /// abort still cost the host's key), instead of only counting calls that ran to completion.</summary>
    public Task ReserveLlmCallAsync(CancellationToken cancellationToken = default)
        => AccumulateAsync(calls: 1, inputTokens: 0, outputTokens: 0, costMicros: 0, mints: 0, cancellationToken);

    /// <summary>Record a completed call's tokens + cost (NOT the call count — that was reserved at the gate).
    /// Called at the tail once a response exists; the token cap and cost trend can only be known then.</summary>
    public Task RecordLlmUsageAsync(long inputTokens, long outputTokens, long costMicros, CancellationToken cancellationToken = default)
        => AccumulateAsync(calls: 0, inputTokens, outputTokens, costMicros, mints: 0, cancellationToken);

    // Voice mints carry no token COST here — voice is flat-priced separately (docs §4); this records the
    // mint count only, so the LLM cost column isn't polluted by a voice session.
    public Task RecordVoiceSessionMintAsync(CancellationToken cancellationToken = default)
        => AccumulateAsync(calls: 0, inputTokens: 0, outputTokens: 0, costMicros: 0, mints: 1, cancellationToken);

    /// <summary>Upsert on the (household, day) row. Race-safe without a transaction: try the in-place
    /// increment first; when the row doesn't exist yet, insert it — and if a concurrent request just
    /// won that insert (the unique index rejects ours), fall back to the increment.</summary>
    private async Task AccumulateAsync(int calls, long inputTokens, long outputTokens, long costMicros, int mints, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var updated = await IncrementAsync(db, today, calls, inputTokens, outputTokens, costMicros, mints, cancellationToken);
        if (updated > 0) return;

        db.AiUsages.Add(new AiUsage
        {
            Day = today,
            Calls = calls,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CostMicros = costMicros,
            VoiceSessionMints = mints,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException
            { SqliteExtendedErrorCode: 2067 or 1555 }) // SQLITE_CONSTRAINT_UNIQUE / _PRIMARYKEY only
        {
            // Unique-index collision: a concurrent request created today's row between our check and
            // insert. Detach our loser and add onto theirs instead. (A different constraint — NOT NULL from
            // a stale schema, say — is a real error and must propagate, not loop uselessly here.)
            db.ChangeTracker.Clear();
            var retried = await IncrementAsync(db, today, calls, inputTokens, outputTokens, costMicros, mints, cancellationToken);
            if (retried == 0)
            {
                logger.LogWarning("AI usage upsert lost both the insert and the retry increment for {Day}.", today);
            }
        }
    }

    private static Task<int> IncrementAsync(
        ShelfAwareDbContext db, DateOnly today, int calls, long inputTokens, long outputTokens, long costMicros, int mints,
        CancellationToken cancellationToken)
        => db.AiUsages.Where(u => u.Day == today).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Calls, u => u.Calls + calls)
                .SetProperty(u => u.InputTokens, u => u.InputTokens + inputTokens)
                .SetProperty(u => u.OutputTokens, u => u.OutputTokens + outputTokens)
                .SetProperty(u => u.CostMicros, u => u.CostMicros + costMicros)
                .SetProperty(u => u.VoiceSessionMints, u => u.VoiceSessionMints + mints),
            cancellationToken);

    private static Task<AiUsage?> TodayRowAsync(ShelfAwareDbContext db, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return db.AiUsages.AsNoTracking().FirstOrDefaultAsync(u => u.Day == today, cancellationToken);
    }
}
