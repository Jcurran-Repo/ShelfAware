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
    // Both blocks in each doc: the Linux one and the Windows (family box) one.
    [Theory]
    [InlineData("deploy-kokoro.md", 2)]
    [InlineData("deploy-moonshine.md", 2)]
    public void Every_pasted_install_checks_its_archive_against_the_hash_the_bootstrap_uses(string doc, int blocks)
    {
        var bootstrap = PastedInstalls.BootstrapFetches();
        var text = File.ReadAllText(RepoTree.FileAt(Path.Combine("docs", doc)));

        // ⚠️ Judged one fenced block at a time (PastedInstalls.In). Searching the whole file for "the
        // first hash after the name" let the Windows block's copy stand in for the Linux block's — so the
        // Linux block with its check DELETED still passed, which is the exact regression this rule exists
        // for. Each block has to carry its own hash, and its own check command, or it fails here.
        var installs = PastedInstalls.In(text);

        // Reach guards: a pattern that stopped matching would otherwise pass having compared nothing —
        // and the bootstrap's own list collapsing would make every block look unknown for the wrong reason.
        Assert.True(bootstrap.Count >= 3,
            $"Only {bootstrap.Count} bootstrap fetch(es) found in deploy-droplet.yml — the scan is broken, not the workflow.");
        Assert.True(installs.Count == blocks,
            $"Found {installs.Count} install block(s) naming an archive in docs/{doc}, expected {blocks} — "
            + "either the scan is broken or a block stopped naming its archive the way the others do.");

        var withoutCheck = installs.Where(p => !p.ChecksItsHash).Select(p => p.Archive).ToList();
        Assert.True(withoutCheck.Count == 0,
            $"An install block in docs/{doc} no longer runs its checksum (sha256sum -c / Get-FileHash): "
            + string.Join(", ", withoutCheck));

        // ⚠️ The bash block runs as root, so it must pin the models directory by
        // inode rather than trust its name — the bootstrap's rule. (The PowerShell block installs under
        // the family box's own user profile, where no second account can re-point anything.)
        var unpinned = installs.Where(p => p.IsBash && !p.PinsModels).Select(p => p.Archive).ToList();
        Assert.True(unpinned.Count == 0,
            $"docs/{doc} has a bash install block that does not pin models/ by inode "
            + "(cd -P \"$M\" + /proc/$$/cwd): " + string.Join(", ", unpinned));

        var wrong = installs.Where(p => !bootstrap.TryGetValue(p.Archive, out var sha) || sha != p.Sha).ToList();
        Assert.True(wrong.Count == 0,
            $"docs/{doc} installs an archive under a hash the deploy's bootstrap does not use for it — "
            + "one has been edited and the other has not:" + Environment.NewLine
            + string.Join(Environment.NewLine, wrong.Select(p => $"{p.Archive} → {p.Sha}")));
    }
}
