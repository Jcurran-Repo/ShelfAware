using System.Text.RegularExpressions;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// Rules about how Razor SOURCE is written that the compiler will not hold for us on every toolchain.
///
/// These exist because a paragraph cannot fail a build. The repo's history is full of constraints that
/// were written down and then broken anyway, sometimes by the session that had just read them
/// (docs/journal/README.md); where a rule can be held by a test instead, it should be.
/// </summary>
public class RazorSourceRulesTests
{
    /// <summary>
    /// ⚠️ A switch-expression arm inside <c>@code</c> must not OPEN with a bare relational pattern
    /// (<c>&lt; 0 =&gt;</c>, <c>&gt;= 5 =&gt;</c>). The Razor parser decides markup-vs-code at the start of a
    /// line, so a leading <c>&lt;</c> reads as a tag: on the SDK this was found with (10.0.112) five such
    /// arms produced 273 errors across four pages, at a commit CI reported green on a different toolchain.
    /// Write the guard form instead — <c>_ when days &lt; 0 =&gt;</c> — which is equivalent and parses
    /// everywhere.
    ///
    /// The fix is cheap and the failure is not: it presents as hundreds of tag-mismatch errors in files
    /// nobody touched, which reads as a broken repo rather than a parser disagreement.
    /// </summary>
    [Fact]
    public void No_razor_file_opens_a_switch_arm_with_a_bare_relational_pattern()
    {
        // operator, then a constant-shaped operand, then `=>`. Deliberately narrow: a markup line like
        // `<p>@(x => y)</p>` must not match, so the operand may not contain `<` or `>`.
        var arm = new Regex("""^\s*(<=|>=|<|>)\s*(-?[\w.]+|'[^']*'|"[^"]*")\s*=>""");

        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in RazorFiles())
        {
            scanned++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (arm.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} — {lines[i].Trim()}");
        }

        // Without this the test passes vacuously the moment the path walk breaks (CI layout change, a
        // moved project): green would be what the defect produces, which is the trap this suite has hit
        // before. 40-odd .razor files exist today; any collapse toward zero is a broken scan, not a clean repo.
        Assert.True(scanned > 20, $"Only {scanned} .razor file(s) were scanned — the source walk is broken, not the sources.");

        Assert.True(offenders.Count == 0,
            "A switch arm opens with a bare relational pattern, which Razor reads as a tag start. "
            + "Use `_ when <expr> <op> <value> =>` instead:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Every <c>.razor</c> under <c>src/</c>, found by walking up to the solution file — so the
    /// test does not depend on the build's output layout.</summary>
    private static IEnumerable<string> RazorFiles() =>
        Directory.EnumerateFiles(RepoTree.DirectoryAt("src"), "*.razor", SearchOption.AllDirectories);
}
