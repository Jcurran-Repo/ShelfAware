using System.Text.RegularExpressions;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The recorded sha256 of every voice model archive is written in two places — <c>sha_for()</c> in
/// <c>.github/workflows/voice-bakeoff.yml</c>, and the copy-paste instructions in
/// <c>docs/voice-bakeoff.md</c> — and this is what stops them drifting apart.
///
/// <para>⚠️ Duplicated deliberately, and only because this test exists. The doc's copy is the one a
/// person runs AS ROOT on a box holding real households' receipts, so it has to be complete enough to
/// paste; telling an operator to go and read a YAML file for the hash is how a hash gets skipped. But
/// two sites answering the same question is the failure CLAUDE.md names as this repo's most expensive,
/// and the honest fix for a fact that must be written twice is a rule that fails the build when the two
/// disagree — not a sentence promising they cannot.</para>
///
/// <para>The workflow verifies its own downloads against <c>sha_for()</c> at run time, so this test is
/// what extends that guarantee to the documented commands.</para>
/// </summary>
public class VoiceModelHashRulesTests
{
    private static readonly Regex Sha = new("\\b[0-9a-f]{64}\\b");

    [Fact]
    public void Every_hash_in_the_voice_docs_is_one_the_bake_off_workflow_records()
    {
        var recorded = HashesIn(RepoFile(Path.Combine(".github", "workflows", "voice-bakeoff.yml")));
        var documented = HashesIn(RepoFile(Path.Combine("docs", "voice-bakeoff.md")));

        // Reach guards, separate from the finding: a regex that stopped matching, or a file that moved,
        // would otherwise make this pass by scanning nothing — the exact way a rule ships having tested
        // zero of what it claims to cover (docs/journal/build-log.md, the mutation gate that ran no
        // mutants). Ten archives plus a vocoder are recorded; the docs paste three of them.
        Assert.True(recorded.Count >= 11,
            $"Only {recorded.Count} sha256 value(s) found in voice-bakeoff.yml — the scan is broken, not the workflow.");
        Assert.True(documented.Count >= 3,
            $"Only {documented.Count} sha256 value(s) found in docs/voice-bakeoff.md — the scan is broken, not the doc.");

        var stale = documented.Except(recorded).ToList();

        Assert.True(stale.Count == 0,
            "docs/voice-bakeoff.md names a sha256 the bake-off workflow does not record, so one of the two "
            + "has been edited and the other has not. The documented commands are run as root on a box "
            + "holding real data — fix them together:" + Environment.NewLine
            + string.Join(Environment.NewLine, stale));
    }

    private static IReadOnlyCollection<string> HashesIn(string path) =>
        [.. Sha.Matches(File.ReadAllText(path)).Select(m => m.Value).Distinct(StringComparer.Ordinal)];

    /// <summary>A path inside the repository, found by walking up to the solution file — so the test does
    /// not depend on the build's output layout.</summary>
    private static string RepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShelfAware.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir); // no solution above the test assembly — the walk is wrong, not the sources

        var file = Path.Combine(dir!.FullName, relativePath);
        Assert.True(File.Exists(file), $"{relativePath} is not where this test expects it.");
        return file;
    }
}
