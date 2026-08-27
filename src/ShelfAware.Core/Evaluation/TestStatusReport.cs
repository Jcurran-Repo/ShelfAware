using System.Text.Json.Serialization;

namespace ShelfAware.Core.Evaluation;

/// <summary>One test project's run counts (from its .trx summary).</summary>
public sealed record TestProjectResult(string Name, int Total, int Passed, int Failed, int Skipped);

/// <summary>
/// The test-suite snapshot the admin dashboard's "Tests &amp; quality" card renders — written by CI from a
/// real run and read back like <see cref="EvalResults"/> reads eval-results.json. Deliberately CI-fed, not
/// hardcoded: a number typed into a page rots the moment the suite changes, and this repo's history is full
/// of exactly that. The commit sha + generated-at are shown so any staleness is visible rather than implied.
/// </summary>
public sealed record TestStatusReport
{
    public DateTimeOffset GeneratedAt { get; init; }
    public string CommitSha { get; init; } = "";
    public string Branch { get; init; } = "";

    /// <summary>Link to the GitHub Actions run that produced this, when CI set it.</summary>
    public string? RunUrl { get; init; }

    /// <summary>Build warnings from the run, when CI captured them (null = not recorded this run).</summary>
    public int? Warnings { get; init; }

    /// <summary>Core mutation score as a percentage, when known (null = not part of this run — the mutation
    /// suite is weekly and separate from the per-push test run).</summary>
    public double? MutationScore { get; init; }

    public IReadOnlyList<TestProjectResult> Projects { get; init; } = [];

    [JsonIgnore] public int TotalTests => Projects.Sum(p => p.Total);
    [JsonIgnore] public int TotalPassed => Projects.Sum(p => p.Passed);
    [JsonIgnore] public int TotalFailed => Projects.Sum(p => p.Failed);
    [JsonIgnore] public int TotalSkipped => Projects.Sum(p => p.Skipped);

    /// <summary>A green suite: at least one test ran and none failed.</summary>
    [JsonIgnore] public bool AllPassed => TotalTests > 0 && TotalFailed == 0;

    [JsonIgnore] public string ShortSha => CommitSha.Length >= 7 ? CommitSha[..7] : CommitSha;
}
