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
        /// <summary>A bash block — the one run as root on the droplet. (The PowerShell blocks install
        /// under the family box's own user profile, where no second account can re-point anything.)</summary>
        internal bool IsBash => Code.Contains("sha256sum -c", StringComparison.Ordinal);

        /// <summary>It runs its checksum and STOPS on a mismatch: in bash, <c>sha256sum -c</c> joined to a
        /// clean-up-and-fail (a check on a line of its own prints FAILED and lets the next line run); in
        /// PowerShell, <c>Get-FileHash</c> compared and a <c>throw</c>.</summary>
        internal bool ChecksItsHash => IsBash ? ChainedCheck.IsMatch(Code) : ThrowingHashCheck.IsMatch(Code);

        /// <summary>It pins the models directory by inode — cd into it, then the kernel's view of where
        /// that landed must be exactly that path, root-owned and 755 — AND checks the parent is root's and
        /// writable by nobody else, or it refuses; then checks again before it reports success. The
        /// bootstrap's rule (<c>.github/workflows/deploy-droplet.yml</c>), whole.
        /// <para>⚠️ The parent half is not decoration. The models were moved out of the service account's
        /// home and into <c>/opt</c> precisely so that nothing but root can re-point the name, which makes
        /// the parent's ownership the property the whole location rests on — an <c>/opt</c> someone had
        /// made group-writable would hand back the exposure the move was made to close, silently.</para></summary>
        internal bool PinsModels =>
            Code.Contains("cd -P \"$M\"", StringComparison.Ordinal)
            && PinGuard.IsMatch(Code)
            && ClosingRecheck.IsMatch(Code);
    }

    private static readonly Regex ChainedCheck = new(
        @"sha256sum -c - \\\r?\n\s*\|\| \{ rm -rf ""\$T""; false; \}");

    // The mismatch branch itself must throw — a throw somewhere else in the block (there are several)
    // would let a Write-Warning here stand in for it.
    private static readonly Regex ThrowingHashCheck = new(
        @"Get-FileHash [^\r\n]*-ne '[0-9a-f]{64}'\) \{\s*\r?\n\s*Remove-Item \$archive\s*\r?\n\s*throw ");

    // Ends with the continuation into the next step: a guard whose "&&" onward was dropped still parses
    // and still prints its refusal — and then lets the install carry on.
    private static readonly Regex PinGuard = new(
        @"\{ \{ \[ ""\$\(readlink ""/proc/\$\$/cwd""\)"" = ""\$M"" \] && \[ ""\$\(stat -c %u:%a \.\)"" = 0:755 \] \\\r?\n"
        + @"\s*&& \[ ""\$\(stat -c %u \.\.\)"" = 0 \] \\\r?\n"
        + @"\s*&& \[ -z ""\$\(find \.\. -maxdepth 0 \\\( -perm -020 -o -perm -002 \\\) -print\)"" \]; \} \\\r?\n"
        + @"\s*\|\| \{ echo [^\r\n]*; false; \}; \} \\\r?\n\s*&& ");

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
        Regex.Matches(Bootstrap(), @"^\s*fetch\s+([\w.\-]+)\s*\\\s*\r?\n\s*([0-9a-f]{64})", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

    /// <summary>Where a Linux box keeps its models, as the deploy's bootstrap defines it:
    /// <c>MODELS=&lt;path&gt;</c>. The one definition — every pasted install and every documented
    /// <c>Speech__*__ModelDirectory</c> is held to it by <c>ModelsRootRulesTests</c>.</summary>
    internal static string BootstrapModelsRoot()
    {
        var found = Regex.Match(Bootstrap(), @"^\s*MODELS=(\S+)\s*$", RegexOptions.Multiline);
        Assert.True(found.Success,
            "deploy-droplet.yml's bootstrap no longer sets MODELS=<path>, so there is nothing to hold the "
            + "docs and the env examples to — and every rule that asks this would pass having compared "
            + "against an empty string.");
        return found.Groups[1].Value;
    }

    private static string Bootstrap() =>
        File.ReadAllText(RepoTree.FileAt(Path.Combine(".github", "workflows", "deploy-droplet.yml")));
}
