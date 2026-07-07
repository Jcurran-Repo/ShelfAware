using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Llm;
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
    ILogger<AiUsageMeter> logger)
{
    public sealed record TodayUsage(int Calls, long Tokens, int VoiceSessionMints);

    public async Task<TodayUsage> GetTodayAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await TodayRowAsync(db, cancellationToken);
        return row is null
            ? new TodayUsage(0, 0, 0)
            : new TodayUsage(row.Calls, row.InputTokens + row.OutputTokens, row.VoiceSessionMints);
    }

    /// <summary>Throws (with user-presentable text — the AI surfaces show exception-adjacent friendly
    /// errors) when today's LLM usage has reached a configured cap. Call BEFORE the provider call.</summary>
    public async Task EnsureLlmCallAllowedAsync(CancellationToken cancellationToken = default)
    {
        if (llm.Value.DailyCallLimit is null && llm.Value.DailyTokenLimit is null) return;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await TodayRowAsync(db, cancellationToken);
        if (row is null) return;

        if (llm.Value.DailyCallLimit is int callLimit && row.Calls >= callLimit)
        {
            throw new InvalidOperationException(
                "Today's AI allowance on this server is used up — it resets tomorrow. " +
                "(Bringing your own key in Settings is never limited.)");
        }
        if (llm.Value.DailyTokenLimit is long tokenLimit && row.InputTokens + row.OutputTokens >= tokenLimit)
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
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await TodayRowAsync(db, cancellationToken);
        return row is null || row.VoiceSessionMints < limit;
    }

    public Task RecordLlmCallAsync(long inputTokens, long outputTokens, CancellationToken cancellationToken = default)
        => AccumulateAsync(calls: 1, inputTokens, outputTokens, mints: 0, cancellationToken);

    public Task RecordVoiceSessionMintAsync(CancellationToken cancellationToken = default)
        => AccumulateAsync(calls: 0, inputTokens: 0, outputTokens: 0, mints: 1, cancellationToken);

    /// <summary>Upsert on the (household, day) row. Race-safe without a transaction: try the in-place
    /// increment first; when the row doesn't exist yet, insert it — and if a concurrent request just
    /// won that insert (the unique index rejects ours), fall back to the increment.</summary>
    private async Task AccumulateAsync(int calls, long inputTokens, long outputTokens, int mints, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var updated = await IncrementAsync(db, today, calls, inputTokens, outputTokens, mints, cancellationToken);
        if (updated > 0) return;

        db.AiUsages.Add(new AiUsage
        {
            Day = today,
            Calls = calls,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            VoiceSessionMints = mints,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        {
            // Unique-index collision: a concurrent request created today's row between our check and
            // insert. Detach our loser and add onto theirs instead.
            db.ChangeTracker.Clear();
            var retried = await IncrementAsync(db, today, calls, inputTokens, outputTokens, mints, cancellationToken);
            if (retried == 0)
            {
                logger.LogWarning("AI usage upsert lost both the insert and the retry increment for {Day}.", today);
            }
        }
    }

    private static Task<int> IncrementAsync(
        ShelfAwareDbContext db, DateOnly today, int calls, long inputTokens, long outputTokens, int mints,
        CancellationToken cancellationToken)
        => db.AiUsages.Where(u => u.Day == today).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Calls, u => u.Calls + calls)
                .SetProperty(u => u.InputTokens, u => u.InputTokens + inputTokens)
                .SetProperty(u => u.OutputTokens, u => u.OutputTokens + outputTokens)
                .SetProperty(u => u.VoiceSessionMints, u => u.VoiceSessionMints + mints),
            cancellationToken);

    private static Task<AiUsage?> TodayRowAsync(ShelfAwareDbContext db, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return db.AiUsages.AsNoTracking().FirstOrDefaultAsync(u => u.Day == today, cancellationToken);
    }
}
