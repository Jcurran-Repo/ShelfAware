using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ShelfAware.Tests;

/// <summary>
/// ⚠️ Cover for the walk the build rules stand on — because a scanner that quietly reads less than it
/// claims is the exact failure those rules exist to prevent, and this repo has shipped it twice: once as
/// a <c>*.cs</c> glob that could not see a <c>.razor</c> file, and once as a razor loop pointed at a
/// scope containing no razor files at all. Both went green. Neither could have failed.
/// </summary>
public class SourceTreeTests
{
    /// <summary>⚠️ The exemption list cannot go stale in either direction. A name on it that lifts fine
    /// is an exemption hiding a file the rules could be judging; a name on it that is gone is a list
    /// nobody has read. The other direction — a NEW unliftable page — is caught by
    /// <see cref="SourceTree.Of"/> itself, which fails rather than skipping an unknown broken lift.</summary>
    [Fact]
    public void The_only_unliftable_pages_are_the_ones_named_here()
    {
        var pages = Directory.EnumerateFiles(
            RepoTree.DirectoryAt(Path.Combine("src", "ShelfAware.Web", "Components")),
            "*.razor", SearchOption.AllDirectories).ToList();

        Assert.True(pages.Count > 50, $"Only {pages.Count} page(s) found — the walk is broken, not the sources.");

        var unliftable = pages
            .Where(p => SourceTree.CodeBlockOf(File.ReadAllText(p)) is { } code
                        && CSharpSyntaxTree.ParseText($"class RazorCodeBlock {{{code}}}")
                            .GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            .Select(p => Path.GetFileName(p)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            SourceTree.Unliftable.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            unliftable);
    }

    /// <summary>⚠️ The lift's own behaviour, on snippets rather than on the repo — so it cannot start
    /// returning null for every file and leave every rule passing over nothing.</summary>
    [Theory]
    [InlineData("<p>hi</p>", null)]                                    // no @code block at all
    [InlineData("<p>hi</p>\n@code {\n    int x;\n}", "\n    int x;\n")]
    [InlineData("@code {\n    void M() { if (x) { y(); } }\n}", "\n    void M() { if (x) { y(); } }\n")]
    public void The_lift_finds_the_code_block_or_says_it_found_none(string razor, string? expected) =>
        Assert.Equal(expected, SourceTree.CodeBlockOf(razor));

    /// <summary>⚠️ A file whose braces the lift misreads produces a tree with ERRORS, not an exception —
    /// the fact the caller's diagnostic check depends on, and the one the lift's doc comment used to
    /// deny. Pinned directly, because if Roslyn ever did throw instead, the check above would be dead
    /// code and every razor file would silently stop being judged.</summary>
    [Fact]
    public void A_misread_block_parses_with_errors_rather_than_throwing()
    {
        var truncated = SourceTree.CodeBlockOf("@code {\n    void M() {\n");
        Assert.Null(truncated); // unbalanced: the lift declines rather than guessing

        var tree = CSharpSyntaxTree.ParseText("class RazorCodeBlock { void M() { if ( }");
        Assert.Contains(tree.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }
}
