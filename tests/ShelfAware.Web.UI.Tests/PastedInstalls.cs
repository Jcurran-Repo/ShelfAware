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
    /// in bash, <c>$name = '&lt;archive&gt;'</c> in PowerShell — the first hash in it, and its CODE.
    /// <para>⚠️ Code, not text: <see cref="In"/> drops every comment line before anything is matched.
    /// The blocks explain themselves at length, and an explanation that mentions <c>/proc/$$/cwd</c> or
    /// <c>sha256sum -c</c> must not count as doing it — a review deleted the real guard from four blocks
    /// and watched a text-matching version of these rules stay green.</para></summary>
    internal sealed record Block(string Archive, string Sha, string Code)
    {
        /// <summary>A bash block — run as root on the droplet, into the service account's home.</summary>
        internal bool IsBash => Code.Contains("sha256sum -c", StringComparison.Ordinal);

        /// <summary>It runs its checksum and STOPS on a mismatch: in bash, <c>sha256sum -c</c> joined to a
        /// clean-up-and-fail (a check on a line of its own prints FAILED and lets the next line run); in
        /// PowerShell, <c>Get-FileHash</c> compared and a <c>throw</c>.</summary>
        internal bool ChecksItsHash => IsBash
            ? ChainedCheck.IsMatch(Code)
            : Code.Contains("Get-FileHash", StringComparison.Ordinal) && Code.Contains("throw", StringComparison.Ordinal);

        /// <summary>It pins models/ by inode — cd into it, then the kernel's view of where that landed must
        /// be exactly that path, root-owned and 755, or it refuses — and checks again before it reports
        /// success. The bootstrap's rule (<c>.github/workflows/deploy-droplet.yml</c>), whole.</summary>
        internal bool PinsModels =>
            Code.Contains("cd -P \"$M\"", StringComparison.Ordinal)
            && PinGuard.IsMatch(Code)
            && ClosingRecheck.IsMatch(Code);
    }

    private static readonly Regex ChainedCheck = new(
        @"sha256sum -c - \\\r?\n\s*\|\| \{ rm -rf ""\$T""; false; \}");

    private static readonly Regex PinGuard = new(
        @"\{ \{ \[ ""\$\(readlink ""/proc/\$\$/cwd""\)"" = ""\$M"" \] && \[ ""\$\(stat -c %u:%a \.\)"" = 0:755 \]; \} \\\r?\n"
        + @"\s*\|\| \{ echo [^\r\n]*; false; \}; \}");

    private static readonly Regex ClosingRecheck = new(
        @"\{ \[ ""\$\(readlink ""/proc/\$\$/cwd""\)"" = ""\$M"" \] \\\r?\n\s*\|\| \{ echo [^\r\n]*; false; \}; \} \\\r?\n\s*&& ls ""\./\$V""");

    /// <summary>Every install block in a doc, judged one fenced block at a time — so no block's hash can
    /// stand in for another's — and on its code alone, comment lines removed.</summary>
    internal static IReadOnlyList<Block> In(string docText) =>
        [.. Regex.Matches(docText, @"```\w*\r?\n(.*?)```", RegexOptions.Singleline)
            .Select(b => CodeOf(b.Groups[1].Value))
            .Select(code => (Code: code, Name: Regex.Match(code, @"(?:\bV=|\$name\s*=\s*')([\w.\-]+)")))
            .Where(b => b.Name.Success)
            .Select(b => new Block(b.Name.Groups[1].Value, Sha.Match(b.Code).Value, b.Code))];

    /// <summary>A block with its comment lines (bash and PowerShell both start them with <c>#</c>)
    /// removed.</summary>
    private static string CodeOf(string body) =>
        string.Join('\n', body.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>The archives the droplet deploy's bootstrap fetches, each with the hash it checks:
    /// <c>fetch &lt;archive&gt; \</c>, then the sha on the next line.</summary>
    internal static Dictionary<string, string> BootstrapFetches() =>
        Regex.Matches(
                File.ReadAllText(RepoTree.FileAt(Path.Combine(".github", "workflows", "deploy-droplet.yml"))),
                @"^\s*fetch\s+([\w.\-]+)\s*\\\s*\r?\n\s*([0-9a-f]{64})", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
}
