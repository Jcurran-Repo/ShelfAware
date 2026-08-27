using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShelfAware.Core.Evaluation;

// Folds the test-run .trx files into the test-status.json the admin dashboard reads.
//   Usage: TestStatusGen <results-dir> <output-json>
// Every *.trx in <results-dir> becomes one project (its file name is the project label). Commit/branch/run
// metadata comes from the GitHub Actions environment; build-warning and mutation-score come from optional
// env vars (BUILD_WARNINGS, MUTATION_SCORE) so a run that doesn't measure them simply omits them.

var resultsDir = args.Length > 0 ? args[0] : "TestResults";
var outputPath = args.Length > 1 ? args[1] : "test-status.json";

var projects = new List<TestProjectResult>();
if (Directory.Exists(resultsDir))
{
    foreach (var trx in Directory.EnumerateFiles(resultsDir, "*.trx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
    {
        projects.Add(TrxSummary.Parse(Path.GetFileNameWithoutExtension(trx), File.ReadAllText(trx)));
    }
}

static string Env(string key) => Environment.GetEnvironmentVariable(key) ?? "";

var server = Env("GITHUB_SERVER_URL");
var repo = Env("GITHUB_REPOSITORY");
var runId = Env("GITHUB_RUN_ID");
string? runUrl = server.Length > 0 && repo.Length > 0 && runId.Length > 0
    ? $"{server}/{repo}/actions/runs/{runId}"
    : null;

int? warnings = int.TryParse(Env("BUILD_WARNINGS"), CultureInfo.InvariantCulture, out var w) ? w : null;
double? mutation = double.TryParse(Env("MUTATION_SCORE"), CultureInfo.InvariantCulture, out var m) ? m : null;

var report = new TestStatusReport
{
    GeneratedAt = DateTimeOffset.UtcNow,
    CommitSha = Env("GITHUB_SHA"),
    Branch = Env("GITHUB_REF_NAME"),
    RunUrl = runUrl,
    Warnings = warnings,
    MutationScore = mutation,
    Projects = projects,
};

var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
});

var full = Path.GetFullPath(outputPath);
Directory.CreateDirectory(Path.GetDirectoryName(full)!);
File.WriteAllText(full, json);
Console.WriteLine($"Wrote {full}: {report.TotalTests} tests across {projects.Count} project(s), {report.TotalFailed} failing.");
