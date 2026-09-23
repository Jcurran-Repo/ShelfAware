using System.Text.RegularExpressions;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The recorded sha256 of a voice model archive is written in more than one place — <c>sha_for()</c>
/// in <c>.github/workflows/voice-bakeoff.yml</c>, the copy-paste instructions in
/// <c>docs/voice-bakeoff.md</c> and <c>docs/deploy-piper.md</c>, and the deploy's own bootstrap — and
/// this is what stops them drifting apart.
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
    [Fact]
    public void Every_hash_in_the_voice_docs_is_one_the_bake_off_workflow_records()
    {
        var recorded = HashesIn(RepoTree.FileAt(Path.Combine(".github", "workflows", "voice-bakeoff.yml")));
        var documented = HashesIn(RepoTree.FileAt(Path.Combine("docs", "voice-bakeoff.md")));

        // Reach guards, separate from the finding: a regex that stopped matching, or a file that moved,
        // would otherwise make this pass by scanning nothing — the exact way a rule ships having tested
        // zero of what it claims to cover (docs/journal/build-log.md, the mutation gate that ran no
        // mutants). The floors are what existed when they were written -- ten archives plus a vocoder
        // recorded, three of them pasted in the docs -- and the lineup only grows.
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

    /// <summary>The bootstrap's fetches that are NOT voices, so not the bake-off's to record — named, so
    /// that a voice added to the bootstrap without a <c>sha_for()</c> row fails below instead of dropping
    /// out of the comparison unnoticed.</summary>
    private static readonly string[] BootstrapFetchesThatAreNotVoices = ["sherpa-onnx-moonshine-tiny-en-int8"];

    /// <summary>
    /// The droplet deploy's <c>bootstrap</c> fetches the demo box's voices with hashes of its own — more
    /// copies of facts <c>sha_for()</c> already records, and the ones that run as root on every deploy. So
    /// they are held to the workflow by ARCHIVE: a fetch of a voice must name the hash <c>sha_for()</c>
    /// gives that voice, and every fetch must be either a recorded voice or named above as something else.
    /// </summary>
    [Fact]
    public void Every_voice_the_deploy_fetches_carries_the_hash_the_bake_off_records()
    {
        var recorded = RecordedByArchive(File.ReadAllText(RepoTree.FileAt(Path.Combine(".github", "workflows", "voice-bakeoff.yml"))));
        var fetched = PastedInstalls.BootstrapFetches();

        // Reach guard: the bootstrap fetches three voices (two Piper, one Kokoro) plus the ear, and a
        // pattern that stopped matching would otherwise pass having compared nothing.
        Assert.True(fetched.Count >= 4,
            $"Only {fetched.Count} bootstrap fetch(es) found in deploy-droplet.yml — the scan is broken, not the workflow.");

        var unaccounted = fetched.Keys
            .Where(a => !recorded.ContainsKey(a) && !BootstrapFetchesThatAreNotVoices.Contains(a))
            .ToList();
        Assert.True(unaccounted.Count == 0,
            "deploy-droplet.yml fetches an archive voice-bakeoff.yml's sha_for() does not record. A voice "
            + "belongs in the bake-off with its hash; anything else belongs in BootstrapFetchesThatAreNotVoices:"
            + Environment.NewLine + string.Join(Environment.NewLine, unaccounted));

        var disagreeing = fetched.Keys.Where(a => recorded.TryGetValue(a, out var sha) && sha != fetched[a]).ToList();
        Assert.True(disagreeing.Count == 0,
            "deploy-droplet.yml fetches an archive under a different sha256 than voice-bakeoff.yml records "
            + "for it — one has been edited and the other has not:" + Environment.NewLine
            + string.Join(Environment.NewLine, disagreeing));
    }

    /// <summary>
    /// The pasteable install blocks name their archive once, as <c>V=&lt;archive&gt;</c>, and check it
    /// against the first hash after that line. Held to <c>sha_for()</c> by that PAIR, not merely by the
    /// hash being one the workflow knows somewhere: a block that named one voice and pasted another's hash
    /// would fail its own check on the box (safely), but it would also be a block nobody could use.
    /// </summary>
    [Theory]
    [InlineData("deploy-piper.md", 1)]
    [InlineData("voice-bakeoff.md", 2)]
    public void Every_pasted_install_checks_its_archive_against_the_hash_recorded_for_it(string doc, int atLeast)
    {
        var recorded = RecordedByArchive(File.ReadAllText(RepoTree.FileAt(Path.Combine(".github", "workflows", "voice-bakeoff.yml"))));
        var text = File.ReadAllText(RepoTree.FileAt(Path.Combine("docs", doc)));

        var pairs = PastedInstalls.In(text);

        Assert.True(pairs.Count >= atLeast,
            $"Only {pairs.Count} install block(s) found in docs/{doc} — the scan is broken, not the doc.");

        // ⚠️ Every block runs as root into the service account's home, so each must check its hash AND
        // pin models/ by inode rather than trust its name — the bootstrap's rule (deploy-droplet.yml).
        // A block written the old way, cd'ing by name and chmod'ing a path, is how that rule came to be
        // half-converted once already; this is what stops it happening again.
        var unsafeBlocks = pairs.Where(p => !p.IsBash || !p.PinsModels).Select(p => p.Archive).ToList();
        Assert.True(unsafeBlocks.Count == 0,
            $"docs/{doc} has an install block that does not both check its hash and pin models/ by inode "
            + "(cd -P \"$M\" + /proc/$$/cwd): " + string.Join(", ", unsafeBlocks));

        var wrong = pairs.Where(p => !recorded.TryGetValue(p.Archive, out var sha) || sha != p.Sha).ToList();
        Assert.True(wrong.Count == 0,
            $"docs/{doc} installs an archive under a hash sha_for() does not record for it:" + Environment.NewLine
            + string.Join(Environment.NewLine, wrong.Select(p => $"{p.Archive} → {p.Sha}")));
    }

    /// <summary><c>sha_for()</c>'s arms: <c>archive) echo &lt;sha&gt;;;</c>.</summary>
    private static Dictionary<string, string> RecordedByArchive(string workflow) =>
        Regex.Matches(workflow, @"^\s*([\w.\-]+)\)\s+echo\s+([0-9a-f]{64});;", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

    private static IReadOnlyCollection<string> HashesIn(string path) =>
        [.. PastedInstalls.Sha.Matches(File.ReadAllText(path)).Select(m => m.Value).Distinct(StringComparer.Ordinal)];
}
