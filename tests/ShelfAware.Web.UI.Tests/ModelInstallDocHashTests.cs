using System.Text.RegularExpressions;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The by-hand model installs in the deploy docs paste the same sha256 the droplet deploy's
/// <c>bootstrap</c> checks — the Linux block and the Windows block each carry their own copy, because a
/// command has to be complete enough to paste, and sending someone to read a workflow file for a hash is
/// how a hash gets skipped. Those copies are run as root on a box holding real households' data, so they
/// are held to the bootstrap here rather than by a sentence promising they agree.
///
/// <para>Paired by ARCHIVE, not merely "is this hash one we know": each block names its archive once —
/// <c>V=&lt;archive&gt;</c> in bash, <c>$name = '&lt;archive&gt;'</c> in PowerShell — and checks it against
/// the hash inside the same fenced block. A block that named one model and pasted another's hash would
/// fail its own check on the box (safely), and would also be a block nobody could use.</para>
/// </summary>
public class ModelInstallDocHashTests
{
    private static readonly Regex Sha = new(@"\b[0-9a-f]{64}\b");

    // Both blocks in each doc: the Linux one and the Windows (family box) one.
    [Theory]
    [InlineData("deploy-kokoro.md", 2)]
    [InlineData("deploy-moonshine.md", 2)]
    public void Every_pasted_install_checks_its_archive_against_the_hash_the_bootstrap_uses(string doc, int blocks)
    {
        var bootstrap = FetchedByArchive(File.ReadAllText(
            RepoTree.FileAt(Path.Combine(".github", "workflows", "deploy-droplet.yml"))));
        var text = File.ReadAllText(RepoTree.FileAt(Path.Combine("docs", doc)));

        // ⚠️ Judged one fenced block at a time. Searching the whole file for "the first hash after the
        // name" let the Windows block's copy stand in for the Linux block's — so the Linux block with its
        // check DELETED still passed, which is the exact regression this rule exists for. Each block has
        // to carry its own hash, and its own check command, or it fails here.
        var installs = Regex.Matches(text, @"```\w*\r?\n(.*?)```", RegexOptions.Singleline)
            .Select(b => b.Groups[1].Value)
            .Select(body => (Body: body, Name: Regex.Match(body, @"(?:\bV=|\$name\s*=\s*')([\w.\-]+)")))
            .Where(b => b.Name.Success)
            .Select(b => (
                Archive: b.Name.Groups[1].Value,
                Sha: Sha.Match(b.Body).Value,
                Checks: b.Body.Contains("sha256sum -c", StringComparison.Ordinal)
                        || b.Body.Contains("Get-FileHash", StringComparison.Ordinal)))
            .ToList();

        // Reach guards: a pattern that stopped matching would otherwise pass having compared nothing —
        // and the bootstrap's own list collapsing would make every block look unknown for the wrong reason.
        Assert.True(bootstrap.Count >= 3,
            $"Only {bootstrap.Count} bootstrap fetch(es) found in deploy-droplet.yml — the scan is broken, not the workflow.");
        Assert.True(installs.Count == blocks,
            $"Found {installs.Count} install block(s) naming an archive in docs/{doc}, expected {blocks} — "
            + "either the scan is broken or a block stopped naming its archive the way the others do.");

        var withoutCheck = installs.Where(p => !p.Checks).Select(p => p.Archive).ToList();
        Assert.True(withoutCheck.Count == 0,
            $"An install block in docs/{doc} no longer runs its checksum (sha256sum -c / Get-FileHash): "
            + string.Join(", ", withoutCheck));

        var wrong = installs.Where(p => !bootstrap.TryGetValue(p.Archive, out var sha) || sha != p.Sha).ToList();
        Assert.True(wrong.Count == 0,
            $"docs/{doc} installs an archive under a hash the deploy's bootstrap does not use for it — "
            + "one has been edited and the other has not:" + Environment.NewLine
            + string.Join(Environment.NewLine, wrong.Select(p => $"{p.Archive} → {p.Sha}")));
    }

    /// <summary>The bootstrap's calls: <c>fetch &lt;archive&gt; \</c>, then the sha on the next line.</summary>
    private static Dictionary<string, string> FetchedByArchive(string workflow) =>
        Regex.Matches(workflow, @"^\s*fetch\s+([\w.\-]+)\s*\\\s*\r?\n\s*([0-9a-f]{64})", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
}
