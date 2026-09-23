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

    /// <summary>Where a walk starts: some way of asking "where is the code I am running from". Any one of
    /// these, PAIRED with the climb below, is the walk — whatever it looks for on the way up.
    ///
    /// <para>⚠️ There is more than one spelling, and the first version of this rule knew only the first.
    /// A copy written with an assembly's own location, or the current directory, anchored on
    /// <c>global.json</c> or <c>.git</c> rather than the solution, matched neither half of
    /// this rule and shipped green — while the paragraph above it claimed the pairing was unavoidable. A
    /// rule is only as wide as its vocabulary, so the vocabulary is a list and not a sentence: add to it
    /// when a new spelling appears rather than trusting the claim.</para></summary>
    private static readonly string[] WaysToAskWhereTheAssemblyIs =
    [
        "AppContext" + ".BaseDirectory",
        "Assembly" + ".Location",
        "Assembly" + ".GetExecutingAssembly",
        "Directory" + ".GetCurrentDirectory",
    ];

    /// <summary>The climb. Paired with any of the above it is a repository-root walk and nothing else.
    ///
    /// <para>Each half alone is ordinary, which is why the pair is the test. <c>ShelfAware.Evals</c> reads
    /// a directory beside its own assembly and never climbs; the Roslyn rules climb syntax nodes and never
    /// ask where the assembly is. Neither is flagged.</para>
    ///
    /// <para>⚠️ Every literal here is spelled in pieces, like <see cref="Marker"/> and for the same
    /// reason — written whole they would sit in this very file and the rule would report ITSELF. The
    /// first version of this array did exactly that.</para></summary>
    private const string TheClimb = "." + "Parent";

    /// <summary>The two projects whose rules read the repository tree, and where all four copies of the
    /// walk lived. Named rather than counted: a bare source count is dominated by the two biggest
    /// projects, so a scan that stopped covering <c>Web.UI.Tests</c> — home to two of the four — would
    /// still clear any count this rule could sensibly assert.</summary>
    private static readonly string[] RuleBearingProjects = ["ShelfAware.Tests", "ShelfAware.Web.UI.Tests"];

    [Fact]
    public void Only_one_file_under_tests_walks_up_to_the_solution()
    {
        var tests = RepoTree.DirectoryAt("tests");
        var offenders = new List<string>();
        var namingTheSolution = new List<string>();
        var projectsScanned = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        var theOneWalkStillNamesIt = false;

        string relativeToTests(string path) =>
            Path.GetRelativePath(tests, path).Replace(Path.DirectorySeparatorChar, '/');

        foreach (var file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            scanned++;
            var relative = relativeToTests(file);
            projectsScanned.Add(relative.Split('/')[0]);

            var text = File.ReadAllText(file);
            var namesTheSolution = text.Contains(Marker, StringComparison.Ordinal);
            var climbs = text.Contains(TheClimb, StringComparison.Ordinal)
                && WaysToAskWhereTheAssemblyIs.Any(w => text.Contains(w, StringComparison.Ordinal));
            if (!namesTheSolution && !climbs) continue;

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
        //
        // The floor is 200 against the 248 sources across six projects counted when this was written:
        // room for the suites to shrink, none for the scan to quietly stop reaching. The count alone is
        // not enough, because it is dominated by the two biggest projects — so the two that hold the
        // rules are named as well, Web.UI.Tests being where two of the four original copies lived.
        Assert.True(scanned > 200,
            $"Only {scanned} test source(s) scanned — the scan is broken, not the tree.");
        var unreached = RuleBearingProjects.Where(p => !projectsScanned.Contains(p)).ToList();
        Assert.True(unreached.Count == 0,
            "The scan never reached " + string.Join(" or ", unreached) + ", where the rules that read the "
            + "tree live. It covered: "
            + string.Join(", ", projectsScanned.OrderBy(p => p, StringComparer.Ordinal)) + ".");
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
            + $"RepoTree.Root(), RepoTree.FileAt() or RepoTree.DirectoryAt() from tests/{TheOneWalk}. It "
            + "is linked into ShelfAware.Tests and ShelfAware.Web.UI.Tests only, so from any other test "
            + $"project add <Compile Include=\"..\\Shared\\RepoTree.cs\" Link=\"Shared\\RepoTree.cs\" /> "
            + "and <Using Include=\"ShelfAware.TestSupport\" /> to its .csproj — linking it is the fix, "
            + "writing the walk again is not:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
