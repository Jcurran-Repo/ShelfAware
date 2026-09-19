using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ShelfAware.Tests;

/// <summary>
/// ⚠️ THE one place the rule "whose cancellation is this?" lives, for every call that leaves the app and
/// waits on a provider.
///
/// <para>An <c>HttpClient</c> timeout does not arrive as a timeout. It arrives as
/// <c>TaskCanceledException</c>, which is an <see cref="OperationCanceledException"/> — the same type a
/// caller's own cancellation arrives as, and indistinguishable from it except by asking whether the
/// caller's token was actually signalled. So a bare <c>catch (OperationCanceledException) { throw; }</c>
/// around a provider call reads "the household went away" over an input that means "the provider is
/// slow", and rethrows it past the plain-copy handler written directly underneath.</para>
///
/// <para>⚠️ What that costs is not billing, which is why no suite caught it. These services are called
/// from Blazor Server event handlers, most of which pass no token, several of which have no
/// <c>catch</c>, and none of which sit under an <c>ErrorBoundary</c> — the app has none. An exception
/// escaping there tears down the circuit: an in-progress receipt review, a meal plan's unsaved edits, a
/// half-filled form, gone. Meanwhile the class summaries promise the opposite ("fails open so a flaky
/// API never blocks tag creation"), and the operator loses the degraded-provider log line that is the
/// only signal the service is limping.</para>
///
/// <para>⚠️ It is held as a build rule rather than as a paragraph because the paragraph was tried. Four
/// advisors were converted in one commit, each carrying its own ten-line copy of this argument, while
/// five sibling sites in the same assembly kept the bare form — including the chat turn, the most
/// expensive act in the app, and the meal-plan reroll, which runs inline on the circuit. Two of those
/// four copies named the wrong call sites. A rule that has to be re-typed at each site is a rule that
/// will be typed wrong, and a partial conversion is a new bug with a green suite over it.</para>
/// </summary>
public class ProviderCancellationSiteTests
{
    /// <summary>The projects and files that talk to a provider and are reached from a UI event handler.</summary>
    private static readonly string[] Scope =
    [
        Path.Combine("src", "ShelfAware.Llm"),
        Path.Combine("src", "ShelfAware.Web", "Services", "RecipeAdapter.cs"),
        Path.Combine("src", "ShelfAware.Web", "Services", "RecipeTagService.cs"),
        Path.Combine("src", "ShelfAware.Web", "Services", "CachingTextToSpeech.cs"),
    ];

    [Fact]
    public void A_cancellation_caught_at_the_provider_boundary_asks_whose_it_was()
    {
        var bare = new List<string>();
        var seen = 0;

        foreach (var (file, tree) in Trees())
            foreach (var clause in tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
            {
                if (clause.Declaration?.Type is not { } type) continue;
                var name = type.ToString();
                if (name is not ("OperationCanceledException" or "TaskCanceledException")) continue;
                seen++;
                // A filter that names IsCancellationRequested is the rule; anything else is a filter
                // answering some other question and is reported the same as no filter at all.
                if (clause.Filter?.FilterExpression.ToString().Contains("IsCancellationRequested") != true)
                    bare.Add($"{Path.GetFileName(file)}:{Line(clause)}");
            }

        // Without this the rule passes vacuously the moment the walk or the scope path breaks — green
        // would be what the defect produces, which is the trap this suite has been caught by before.
        Assert.True(seen > 5, $"Only {seen} cancellation catch(es) found at the provider boundary — the "
                            + "scan is broken, not the sources.");

        Assert.True(bare.Count == 0,
            "A cancellation is caught at the provider boundary without asking whose it was. An HttpClient "
            + "timeout arrives as the same exception type, so this rethrows a PROVIDER failure past the "
            + "plain-copy handler below it and out through a Blazor event handler that has no catch and no "
            + "ErrorBoundary behind it — tearing down the circuit and losing whatever the household had "
            + "on screen. Filter it: `when (<the caller's token>.IsCancellationRequested)`:"
            + Environment.NewLine + string.Join(Environment.NewLine, bare));
    }

    private static int Line(SyntaxNode node) =>
        node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

    private static IEnumerable<(string File, SyntaxTree Tree)> Trees()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShelfAware.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir); // no solution above the test assembly — the walk is wrong, not the sources

        foreach (var entry in Scope)
        {
            var path = Path.Combine(dir!.FullName, entry);
            var files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                : [path];
            foreach (var file in files)
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                Assert.True(File.Exists(file), $"{entry} is not on disk — the scope list is stale, so the "
                                             + "rule is silently guarding less than it claims.");
                yield return (file, CSharpSyntaxTree.ParseText(File.ReadAllText(file)));
            }
        }
    }
}
