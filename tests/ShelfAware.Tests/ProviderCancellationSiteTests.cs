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
///
/// <para>⚠️ And the FIRST version of this rule was itself too narrow, in three ways that each hid a
/// live defect. It inspected only catch clauses that EXIST, so <c>AnthropicRecipeAdvisor</c> — the one
/// provider call in the assembly with no <c>try</c> at all — passed it while <c>Recipes.razor</c> rethrew
/// the escape bare and tore the circuit. It globbed <c>*.cs</c>, so no <c>.razor</c> file was reachable,
/// although the sibling rule ten files away already lifts <c>@code</c> blocks for exactly that reason.
/// And its filter test was a substring match, so <c>when (!ct.IsCancellationRequested)</c> — the precise
/// inversion — passed. All three are closed below. The lesson is that a rule written to end a class of
/// defect gets read as covering the class, so whatever it cannot see has to be closed or written down.</para>
/// </summary>
public class ProviderCancellationSiteTests
{
    /// <summary>The provider boundary itself: every service in <c>ShelfAware.Llm</c> that speaks to a
    /// model, plus the handful of Web services that wrap one and are called from a circuit.
    ///
    /// <para>⚠️ It is a WHOLE PROJECT plus named files rather than a list of files, because the first
    /// version listed four paths under a summary claiming it covered "the files that talk to a provider"
    /// and missed several. It is also deliberately NOT all of <c>src/ShelfAware.Web</c>: an endpoint
    /// handler in <c>Program.cs</c> and a page's catch around a database call both rethrow a cancellation
    /// correctly, because there the caller really did go away. The invariant worth holding is narrower
    /// and stronger — <b>a provider cancellation never leaves this boundary</b> — and once it holds, the
    /// layers above it are safe whatever they do with their own.</para></summary>
    private static readonly string[] Scope =
    [
        Path.Combine("src", "ShelfAware.Llm"),
        Path.Combine("src", "ShelfAware.Web", "Services", "RecipeAdapter.cs"),
        Path.Combine("src", "ShelfAware.Web", "Services", "RecipeTagService.cs"),
        Path.Combine("src", "ShelfAware.Web", "Services", "CachingTextToSpeech.cs"),
        Path.Combine("src", "ShelfAware.Web", "Services", "ReceiptSelfEval.cs"),
    ];

    /// <summary>The call that leaves the app for a model.</summary>
    private const string ProviderCall = "GetResponseAsync";

    [Fact]
    public void A_cancellation_caught_at_the_provider_boundary_asks_whose_it_was()
    {
        var bare = new List<string>();
        var seen = 0;

        foreach (var (file, tree) in Trees())
            foreach (var clause in tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
            {
                // ⚠️ A bare `catch { throw; }` and a `catch (Exception e) when (e is OperationCanceled…)`
                // are the same defect in different clothes, and the first version of this rule walked past
                // both — as it did past a fully-qualified `System.OperationCanceledException`.
                if (!DeclaresCancellation(clause)) continue;
                seen++;
                // A filter that names IsCancellationRequested is the rule; anything else is a filter
                // answering some other question and is reported the same as no filter at all.
                if (!AsksWhose(clause.Filter))
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

    /// <summary>
    /// ⚠️ A provider call must sit inside a <c>try</c> that can read a cancellation — because the rule
    /// above can only judge a catch that EXISTS, and the defect it was written to stop then shipped in
    /// the one class that had no catch at all. <c>Recipes.razor</c> rethrew the escape bare, so a slow
    /// provider tore down the circuit of a household that had just typed a request.
    /// </summary>
    [Fact]
    public void A_provider_call_is_wrapped_where_a_cancellation_can_be_read()
    {
        var naked = new List<string>();
        var calls = 0;

        foreach (var (file, tree) in Trees())
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: ProviderCall }) continue;
                calls++;
                if (!call.Ancestors().OfType<TryStatementSyntax>().Any(x => x.Catches.Any(CanReceiveCancellation)))
                    naked.Add($"{Path.GetFileName(file)}:{Line(call)}");
            }

        Assert.True(calls > 5, $"Only {calls} provider call(s) found — the scan is broken, not the sources.");

        Assert.True(naked.Count == 0,
            "A call to the provider is made with no catch that can read a cancellation, so an HttpClient "
            + "timeout escapes the service entirely and reaches whatever UI handler invoked it — where there "
            + "is no ErrorBoundary and the circuit dies with the household's unsaved work in it. Wrap it "
            + "and fail the act, the way every sibling service does:"
            + Environment.NewLine + string.Join(Environment.NewLine, naked));
    }

    /// <summary>Whether this catch singles cancellation OUT — the clause the "whose was it?" rule judges.
    /// A catch-all that logs and fails open is a different question and not this rule's business; a
    /// catch-all that singles cancellation out in its FILTER is this rule's business wearing a disguise,
    /// which is how the first version of the rule was evadable.</summary>
    private static bool DeclaresCancellation(CatchClauseSyntax clause) =>
        Named(clause) is "OperationCanceledException" or "TaskCanceledException"
        || clause.Filter?.FilterExpression.ToString().Contains("OperationCanceledException") == true
        || clause.Filter?.FilterExpression.ToString().Contains("TaskCanceledException") == true;

    /// <summary>Whether this catch could RECEIVE a cancellation at all — what the wrapping rule needs,
    /// since a service that fails the act in a catch-all is guarded just as well as one that names the
    /// type. ⚠️ A filter narrows what a clause receives, so a filtered catch-all does not count.</summary>
    private static bool CanReceiveCancellation(CatchClauseSyntax clause) =>
        clause.Filter is null
        && (clause.Declaration is null
            || Named(clause) is "OperationCanceledException" or "TaskCanceledException" or "Exception");

    /// <summary>The caught type's simple name, or null when the clause declares none. ⚠️ Simple, so a
    /// fully-qualified <c>System.OperationCanceledException</c> cannot slip past the way it used to.</summary>
    private static string? Named(CatchClauseSyntax clause) =>
        clause.Declaration?.Type.ToString() is { } name ? name[(name.LastIndexOf('.') + 1)..] : null;

    /// <summary>Whether a catch filter actually asks WHOSE cancellation it was: a plain read of some
    /// token's <c>IsCancellationRequested</c>. ⚠️ Not a substring test — the first version was one, so
    /// the exact inversion <c>when (!token.IsCancellationRequested)</c> satisfied it.</summary>
    private static bool AsksWhose(CatchFilterClauseSyntax? filter) =>
        filter is not null && Reads(filter.FilterExpression);

    private static bool Reads(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "IsCancellationRequested" } => true,
        ParenthesizedExpressionSyntax parens => Reads(parens.Expression),
        // `a.IsCancellationRequested || b.IsCancellationRequested` is still asking the question; a
        // negation or anything else is not, and is reported exactly as an absent filter would be.
        BinaryExpressionSyntax binary => Reads(binary.Left) || Reads(binary.Right),
        _ => false,
    };

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

            // ⚠️ .razor too. The bare rethrow that actually tore a circuit lived in an @code block, and
            // the first version of this scan globbed *.cs — so it could not see the file it most needed to,
            // while the sibling rule ten files away had been lifting these blocks all along.
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*.razor", SearchOption.AllDirectories))
                if (CodeBlockOf(File.ReadAllText(file)) is { } code)
                    yield return (file, CSharpSyntaxTree.ParseText($"class RazorCodeBlock {{{code}}}"));
        }
    }

    /// <summary>The body of a <c>.razor</c> file's <c>@code { … }</c> block, by brace matching, or null
    /// when the file has none. Crude on purpose: it only has to find C# that would otherwise be
    /// invisible, and a file whose block it misreads fails to parse rather than passing quietly.</summary>
    private static string? CodeBlockOf(string razor)
    {
        var at = razor.IndexOf("@code", StringComparison.Ordinal);
        if (at < 0) return null;
        var open = razor.IndexOf('{', at);
        if (open < 0) return null;

        var depth = 0;
        for (var i = open; i < razor.Length; i++)
        {
            if (razor[i] == '{') depth++;
            else if (razor[i] == '}' && --depth == 0) return razor[(open + 1)..i];
        }
        return null;
    }
}
