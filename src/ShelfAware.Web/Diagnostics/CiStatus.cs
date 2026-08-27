namespace ShelfAware.Web.Diagnostics;

/// <summary>How a workflow run turned out, collapsed from GitHub's status + conclusion pair into the
/// four states the dashboard styles: a green pass, a red fail, an in-flight run, or anything else
/// (cancelled/skipped/neutral) shown neutrally.</summary>
public enum CiOutcome
{
    Passed,
    Failed,
    Running,
    Other,
}

/// <summary>One GitHub Actions workflow run — the latest of its workflow — as the admin dashboard shows
/// it. Raw status/conclusion are kept so the mapping to <see cref="Outcome"/> has one home.</summary>
public sealed record CiRun(
    string Workflow,
    string Status,
    string? Conclusion,
    string Branch,
    string Sha,
    string Title,
    DateTimeOffset UpdatedAt,
    string Url)
{
    /// <summary>THE mapping from GitHub's (status, conclusion) to a dashboard state. A completed run reads
    /// its conclusion; anything still queued/running is Running; everything else (cancelled, skipped,
    /// neutral, action_required) is Other — shown, but not called a pass or a fail.</summary>
    public CiOutcome Outcome => Status switch
    {
        "completed" => Conclusion switch
        {
            "success" => CiOutcome.Passed,
            "failure" or "timed_out" or "startup_failure" => CiOutcome.Failed,
            _ => CiOutcome.Other,
        },
        "queued" or "in_progress" or "requested" or "waiting" or "pending" => CiOutcome.Running,
        _ => CiOutcome.Other,
    };

    /// <summary>The first 7 of the commit sha (git's short form), or the whole thing if it's already shorter.</summary>
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

/// <summary>The CI card's data: the latest run per workflow, when it was fetched, and — when GitHub
/// couldn't be reached — a short reason instead. Never both: a non-null <see cref="Error"/> means the
/// runs are empty and the card shows the reason.</summary>
public sealed record CiStatus(IReadOnlyList<CiRun> Runs, DateTimeOffset FetchedAt, string? Error);

/// <summary>Reads the deployment's GitHub Actions status for the admin dashboard. A seam so the page can
/// be tested against a fake and the real GitHub-hitting implementation against a stubbed HTTP handler.</summary>
public interface ICiStatusProvider
{
    /// <summary>Whether the CI card is turned on for this deployment (config <c>GitHub:Enabled</c>). When
    /// false the page renders no card and never calls <see cref="GetAsync"/> — a self-host with no
    /// internet, or one that simply doesn't want its admin page pinging GitHub, sets this off.</summary>
    bool Enabled { get; }

    Task<CiStatus> GetAsync(CancellationToken ct = default);
}
