namespace ShelfAware.Tests;

/// <summary>
/// ⚠️ The walk from a test assembly up to the repository root is written ONCE, in
/// <c>tests/Shared/RepoTree.cs</c>, and this is what says so.
///
/// <para>It is four lines, which is exactly why it got copied four times: twice into
/// <c>ShelfAware.Tests</c> and twice into <c>ShelfAware.Web.UI.Tests</c>, the last two in the same week
/// by two pull requests that each wrote "fold these together" into the other's follow-up list. And the
/// four had already drifted — two returned a file and asserted it was there, two returned the bare root
/// and left each caller to combine a path and hope — so whether a rule was protected from scanning a
/// file that had moved depended on which copy its author started from. Every one of these rules exists
/// to stop a scan silently covering nothing, so a copied walk is the one duplication that can disable
/// them all at once.</para>
///
/// <para>A paragraph saying "don't copy this" is what the repo had, and it is what got copied. This is
/// the version that fails the build.</para>
///
/// <para>A rule that genuinely needs to READ the solution file — none does today — would trip this by
/// naming it, and should spell the name in two pieces as this file does, or ask for it through
/// <c>RepoTree</c>. Being made to think about it is the point; a walk written by hand is never the
/// answer.</para>
/// </summary>
public class RepoRootWalkRulesTests
{
    /// <summary>The one file allowed to name the solution. Asserted to contain the marker below, so a
    /// solution rename turns this rule red instead of leaving it hunting something that no longer
    /// exists.</summary>
    private const string TheOneWalk = "Shared/RepoTree.cs";

    /// <summary>The solution file's extension, spelled in two pieces ON PURPOSE: written whole, this file
    /// would be the fifth copy of the thing it is banning and would have to exempt itself. A rule that
    /// needs an exemption for its own source is a rule with a hole the shape of its own scanner.</summary>
    private static readonly string Marker = "." + "slnx";

    /// <summary>The walk itself, for a copy that anchors on something other than the solution. Naming the
    /// solution is how all four copies happened to spell it, but <c>global.json</c> and <c>.git</c> sit at
    /// the repository root too, and a walk anchored on either is the same duplication with the marker
    /// above nowhere in it. What no spelling can avoid is starting at the running assembly's own directory
    /// and climbing to parents: those two together ARE the walk, whatever it looks for on the way up.
    ///
    /// <para>The pair is the test, never either half. <c>ShelfAware.Evals</c> reads a directory beside its
    /// own assembly and never climbs; the Roslyn rules climb syntax nodes and never ask where the assembly
    /// is. Each half alone is ordinary; together they are a repository-root walk and nothing else.</para>
    ///
    /// <para>⚠️ Spelled in pieces, like <see cref="Marker"/> and for the same reason — written whole,
    /// these two strings would sit in this very file and the rule would report ITSELF. The first version
    /// of this array did exactly that.</para></summary>
    private static readonly string[] TheWalkItself = ["AppContext" + ".BaseDirectory", "." + "Parent"];

    [Fact]
    public void Only_one_file_under_tests_walks_up_to_the_solution()
    {
        var tests = RepoTree.DirectoryAt("tests");
        var offenders = new List<string>();
        var namingTheSolution = new List<string>();
        var scanned = 0;
        var theOneWalkStillNamesIt = false;

        foreach (var file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            scanned++;
            var text = File.ReadAllText(file);
            var namesTheSolution = text.Contains(Marker, StringComparison.Ordinal);
            var climbs = TheWalkItself.All(part => text.Contains(part, StringComparison.Ordinal));
            if (!namesTheSolution && !climbs) continue;

            var relative = Path.GetRelativePath(tests, file).Replace(Path.DirectorySeparatorChar, '/');
            if (namesTheSolution) namingTheSolution.Add(relative);
            if (relative == TheOneWalk) theOneWalkStillNamesIt = namesTheSolution;
            else offenders.Add(relative + (namesTheSolution
                ? ""
                : " — climbs from the assembly to its parents, under another anchor"));
        }

        // Three guards, in the order that makes each failure say the true thing. Reach first: a scan
        // pointed somewhere empty. Then the subtler reach case — nothing under tests/ names the solution
        // at all, so this rule is hunting a string that no longer exists and would pass over any number of
        // copies. Only then the finding itself. Getting this order wrong is how a moved file gets reported
        // as a duplicate of itself.
        Assert.True(scanned > 50,
            $"Only {scanned} test source(s) scanned — the scan is broken, not the tree.");
        Assert.True(namingTheSolution.Count > 0,
            "No source under tests/ names the solution file at all, so this rule is looking for something "
            + "that isn't there and would pass however many copies of the walk existed. Either the solution "
            + "was renamed — fix Marker — or the walk has gone; point TheOneWalk at wherever it "
            + "lives now.");
        Assert.True(theOneWalkStillNamesIt,
            $"tests/{TheOneWalk} is not the walk any more. The solution is named in: "
            + string.Join(", ", namingTheSolution) + ". If the walk simply moved, point TheOneWalk there — "
            + "otherwise this rule guards a file that is not the one every other rule depends on.");

        Assert.True(offenders.Count == 0,
            "A test source walks up to the repository root itself instead of asking RepoTree. The four "
            + "copies this replaced had already drifted apart — half of them checked that the path they "
            + "handed back was really there and half did not — and a walk that returns a stale path makes "
            + "a rule scan nothing and pass. Use "
            + $"RepoTree.Root(), RepoTree.FileAt() or RepoTree.DirectoryAt() from tests/{TheOneWalk}:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
