using Microsoft.Extensions.Options;

namespace ShelfAware.Web.Diagnostics;

/// <summary>Config for the admin CI card (<c>GitHub</c> section). Owner/Repo default to this project's
/// public repo; a fork self-hosting points them at its own (or turns the card off). A Token is optional —
/// the runs metadata this reads is public, so unauthenticated works; a token only raises the rate limit,
/// which the cache already keeps us far under.</summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    public bool Enabled { get; set; } = true;
    public string Owner { get; set; } = "Jcurran-Repo";
    public string Repo { get; set; } = "ShelfAware";
    public string? Token { get; set; }
    public int CacheMinutes { get; set; } = 5;
}

/// <summary>
/// Reads the deployment's latest GitHub Actions run per workflow for the admin dashboard. A singleton with
/// a short in-memory cache so the admin page (and every admin) shares one fetch and stays far under
/// GitHub's unauthenticated rate limit. It NEVER throws to the caller: a network error, a non-2xx, or
/// malformed JSON all come back as a <see cref="CiStatus"/> with <see cref="CiStatus.Error"/> set and no
/// runs, so a GitHub blip can never break the admin page. Only successes are cached — an error retries on
/// the next load rather than sticking for the whole TTL.
/// </summary>
public sealed class GitHubCiStatus(
    IHttpClientFactory httpFactory,
    IOptions<GitHubOptions> options,
    ILogger<GitHubCiStatus> logger) : ICiStatusProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CiStatus? _cached;

    public bool Enabled => options.Value.Enabled;

    public async Task<CiStatus> GetAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        var ttl = TimeSpan.FromMinutes(Math.Max(1, opts.CacheMinutes));

        if (Fresh(_cached, ttl) is { } hit) return hit;

        await _lock.WaitAsync(ct);
        try
        {
            // Re-check under the lock: a concurrent caller may have just filled the cache.
            if (Fresh(_cached, ttl) is { } hit2) return hit2;

            var status = await FetchAsync(opts, ct);
            if (status.Error is null) _cached = status; // never cache a failure — retry next load
            return status;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static CiStatus? Fresh(CiStatus? cached, TimeSpan ttl) =>
        cached is not null && DateTimeOffset.Now - cached.FetchedAt < ttl ? cached : null;

    private async Task<CiStatus> FetchAsync(GitHubOptions opts, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"repos/{opts.Owner}/{opts.Repo}/actions/runs?per_page=30");
            if (!string.IsNullOrWhiteSpace(opts.Token))
                req.Headers.Authorization = new("Bearer", opts.Token);

            var client = httpFactory.CreateClient("github");
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new CiStatus([], DateTimeOffset.Now, $"GitHub returned HTTP {(int)resp.StatusCode}");

            var json = await resp.Content.ReadAsStringAsync(ct);
            return new CiStatus(CiStatusParser.Parse(json), DateTimeOffset.Now, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER's own cancellation (teardown) — not a GitHub failure. ⚠️ An HttpClient TIMEOUT
            // also throws OperationCanceledException (a TaskCanceledException), but with the caller's token
            // NOT cancelled — that must fall through to the error state below, or the card hangs on
            // "Loading…" forever (defeating this class's never-throws contract).
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't fetch GitHub Actions status.");
            return new CiStatus([], DateTimeOffset.Now, "couldn't reach GitHub");
        }
    }
}
