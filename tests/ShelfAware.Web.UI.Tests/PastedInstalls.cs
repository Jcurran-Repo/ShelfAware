using System.Text.RegularExpressions;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// What a pasteable model install in the docs IS, and what the deploy's bootstrap fetches — asked by
/// every rule that holds those installs to account (<see cref="ModelInstallDocHashTests"/>,
/// <see cref="VoiceModelHashRulesTests"/>).
///
/// <para>⚠️ ONE definition. Those rules were written in two PRs at once, and each had grown its own
/// parse of "an install block", its own reading of the bootstrap's fetches and its own repo-root walk —
/// and one of those parses already had a hole the other had been fixed for (a later block's hash
/// standing in for a block whose check was deleted). Two definitions of the same fact is the failure
/// CLAUDE.md names as this repo's most expensive; the rules ask this, and differ only in what they
/// assert.</para>
/// </summary>
internal static class PastedInstalls
{
    internal static readonly Regex Sha = new(@"\b[0-9a-f]{64}\b");

    /// <summary>One fenced code block that installs a model: the archive it names once — <c>V=&lt;archive&gt;</c>
    /// in bash, <c>$name = '&lt;archive&gt;'</c> in PowerShell — the first hash INSIDE that block, and the
    /// block itself.</summary>
    internal sealed record Block(string Archive, string Sha, string Body)
    {
        /// <summary>A bash block — run as root on the droplet, into the service account's home.</summary>
        internal bool IsBash => Body.Contains("sha256sum -c", StringComparison.Ordinal);

        /// <summary>It runs its checksum at all (bash or PowerShell).</summary>
        internal bool ChecksItsHash => IsBash || Body.Contains("Get-FileHash", StringComparison.Ordinal);

        /// <summary>It pins models/ by inode rather than trusting the name — the bootstrap's rule
        /// (<c>.github/workflows/deploy-droplet.yml</c>).</summary>
        internal bool PinsModels =>
            Body.Contains("cd -P \"$M\"", StringComparison.Ordinal)
            && Body.Contains("/proc/$$/cwd", StringComparison.Ordinal);
    }

    /// <summary>Every install block in a doc, judged one fenced block at a time — so no block's hash can
    /// stand in for another's.</summary>
    internal static IReadOnlyList<Block> In(string docText) =>
        [.. Regex.Matches(docText, @"```\w*\r?\n(.*?)```", RegexOptions.Singleline)
            .Select(b => b.Groups[1].Value)
            .Select(body => (Body: body, Name: Regex.Match(body, @"(?:\bV=|\$name\s*=\s*')([\w.\-]+)")))
            .Where(b => b.Name.Success)
            .Select(b => new Block(b.Name.Groups[1].Value, Sha.Match(b.Body).Value, b.Body))];

    /// <summary>The archives the droplet deploy's bootstrap fetches, each with the hash it checks:
    /// <c>fetch &lt;archive&gt; \</c>, then the sha on the next line.</summary>
    internal static Dictionary<string, string> BootstrapFetches() =>
        Regex.Matches(
                File.ReadAllText(RepoTree.FileAt(Path.Combine(".github", "workflows", "deploy-droplet.yml"))),
                @"^\s*fetch\s+([\w.\-]+)\s*\\\s*\r?\n\s*([0-9a-f]{64})", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
}
