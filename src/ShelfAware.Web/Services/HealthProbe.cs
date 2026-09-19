using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>What <c>/healthz</c> answers with: healthy, plus the names of any checks that failed. The
/// names are deliberately neutral ("identity", "pantry") — see <see cref="HealthProbe"/> for why the
/// endpoint says which check failed but never why.</summary>
public sealed record HealthReport(bool Healthy, IReadOnlyList<string> Failing);

/// <summary>
/// The box's own answer to the only question an uptime monitor can act on: is it serving, and can it reach
/// both of its databases?
///
/// <para>⚠️ <b>This class is the app's sanctioned second use of the raw <see cref="IDbContextFactory{T}"/>
/// for the pantry context.</b> Everything else goes through <c>IHouseholdDbFactory</c>, which pins the
/// tenancy filter; the raw factory is otherwise bootstrap-only. A health check has no household and wants
/// none — it issues <c>CanConnectAsync</c>, which opens the connection and asks the file whether it is
/// there. It reads no row, touches no tenant table, and returns nothing derived from anyone's data, so
/// there is no filter for it to be missing. Confining that to one named class rather than inlining it in
/// <c>Program.cs</c> is the same move <c>AdminReportReader</c> makes for its <c>IgnoreQueryFilters</c>:
/// a sanctioned exception lives somewhere a reviewer can find it and a test can pin it.</para>
///
/// <para>⚠️ It does NOT check the AI provider. An anonymous endpoint that triggers an outbound provider
/// call is a free amplification surface — a scraper would bill the host key for the privilege of being
/// scraped. Provider reachability is what the error log on /admin is for.</para>
///
/// <para>⚠️ It names WHICH check failed and never why: no paths, no connection strings, no exception text.
/// Same rule as the AI surfaces — the detail goes to the log, which is where the operator reads it, and
/// this one is answering the whole internet.</para>
/// </summary>
public sealed class HealthProbe(
    IDbContextFactory<AuthDbContext> identityFactory,
    IDbContextFactory<ShelfAwareDbContext> pantryFactory,
    ILogger<HealthProbe> logger)
{
    /// <summary>How long a result stands before the databases are asked again. The endpoint is anonymous
    /// and unmetered — deliberately, because rate-limiting a health check is how a monitor learns to report
    /// an outage that isn't happening — so this is what keeps a flood costing one pair of connection opens
    /// instead of one per request. A monitor polling every 30s never sees a cached answer.</summary>
    internal static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(5);

    private sealed record Cached(HealthReport Report, DateTimeOffset At);

    // Reference assignment is atomic, so no lock: two callers racing at the moment the window expires both
    // probe, which costs one extra pair of connection opens and is cheaper than serialising every request
    // behind a semaphore.
    private Cached? _cached;

    public async Task<HealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cached is { } cached && now - cached.At < CacheWindow) return cached.Report;

        var failing = new List<string>();
        if (!await CanReachAsync(identityFactory, "identity", cancellationToken)) failing.Add("identity");
        if (!await CanReachAsync(pantryFactory, "pantry", cancellationToken)) failing.Add("pantry");

        var report = new HealthReport(failing.Count == 0, failing);
        _cached = new Cached(report, now);
        return report;
    }

    private async Task<bool> CanReachAsync<TContext>(
        IDbContextFactory<TContext> factory, string name, CancellationToken ct)
        where TContext : DbContext
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Database.CanConnectAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Error, not Warning: a database this box cannot open is the thing the operator most needs to
            // find in the log, and only Error is captured onto /admin. The caller reports the NAME; the
            // reason stops here.
            logger.LogError(ex, "Health check could not reach the {Database} database.", name);
            return false;
        }
    }
}
