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

    [Fact]
    public void Only_one_file_under_tests_walks_up_to_the_solution()
    {
        var tests = RepoTree.DirectoryAt("tests");
        var offenders = new List<string>();
        var scanned = 0;
        var foundTheOneWalk = false;

        foreach (var file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            scanned++;
            if (!File.ReadAllText(file).Contains(Marker, StringComparison.Ordinal)) continue;

            var relative = Path.GetRelativePath(tests, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == TheOneWalk) foundTheOneWalk = true;
            else offenders.Add(relative);
        }

        // Reach guards, separate from the finding. The first catches a scan pointed somewhere empty; the
        // second catches the subtler one — the walk moved or the solution was renamed, so the marker
        // matches nothing anywhere and this rule passes by finding no copies of a thing that no longer
        // exists. That is the failure mode every rule in this suite is written against.
        Assert.True(scanned > 50, $"Only {scanned} test source(s) scanned — the scan is broken, not the tree.");
        Assert.True(foundTheOneWalk,
            $"tests/{TheOneWalk} no longer names the solution file, so this rule is looking for something "
            + "that isn't there and would pass however many copies of the walk existed. Point it at wherever "
            + "the walk lives now.");

        Assert.True(offenders.Count == 0,
            "A test source walks up to the solution itself instead of asking RepoTree. The four copies "
            + "this replaced had already drifted apart — half of them checked that the path they handed "
            + "back was really there and half did not — and a walk that returns a stale path makes a rule "
            + "scan nothing and pass. Use "
            + $"RepoTree.Root(), RepoTree.FileAt() or RepoTree.DirectoryAt() from tests/{TheOneWalk}:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
