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
/// the escape bare and tore the circuit. It globbed <c>*.cs</c>, so no <c>.razor</c> file was reachable.
/// And its filter test was a substring match, so <c>when (!ct.IsCancellationRequested)</c> — the precise
/// inversion — passed. The lesson is that a rule written to end a class of defect gets read as covering
/// the class, so whatever it cannot see has to be closed or written down.</para>
///
/// <para>⚠️ The SECOND version then made the same mistake one level up, which is why the razor scan
/// below carries a reach guard of its own. It added <c>.razor</c> reading and a comment saying the gap
/// was closed — but pointed it at a scope holding one directory (<c>ShelfAware.Llm</c>, which contains
/// no <c>.razor</c> file) and four single files, so the loop body never executed once and the file it
/// named twice by name was still unseen. Dead code that reads as coverage is worse than a written-down
/// gap, because the next session stops looking. A scan now has to prove it REACHED something, not
/// merely that it found nothing wrong.</para>
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
    private static readonly string[] BoundaryScope =
    [
        Path.Combine("src", "ShelfAware.Llm"),
        Path.Combine("src", "ShelfAware.Web", "Services", "RecipeAdapter.cs"),
        Path.Combine("src", "ShelfAware.Web", "Services", "RecipeTagService.cs"),
        Path.Combine("src", "ShelfAware.Web", "Services", "CachingTextToSpeech.cs"),
        Path.Combine("src", "ShelfAware.Web", "Services", "ReceiptSelfEval.cs"),
    ];

    /// <summary>Where a provider call may be MADE — the boundary, plus every page, because a page that
    /// starts calling a provider directly is the thing the wrapping rule exists to catch.
    /// <para>⚠️ Deliberately wider than <see cref="BoundaryScope"/>, and only the wrapping rule uses it.
    /// The "whose cancellation" rule must NOT judge the pages: sixteen of them carry
    /// <c>catch (Exception ex) when (ex is not OperationCanceledException)</c> around a DATABASE call,
    /// where letting a cancellation past is right because the caller really is the only thing that can
    /// raise one. At a provider boundary the same clause is the defect itself — a provider timeout
    /// arrives as that exact type and the filter waves it straight out through the event handler — so
    /// one scope cannot serve both rules, and widening the boundary scope to reach razor would have made
    /// the rule fail on correct code and invited narrowing it back.</para>
    /// <para>⚠️ Also deliberately NOT the <c>IChatClient</c> decorators (<c>MeteredChatClient</c>,
    /// <c>ByokChatClient</c>): they pass a call through to the client beneath them, and the service that
    /// OWNS the call is the one that must guard it. Requiring a try in each decorator would add catches
    /// that can only re-report what the owner already handles.</para></summary>
    private static readonly string[] CallScope =
        [.. BoundaryScope, Path.Combine("src", "ShelfAware.Web", "Components")];

    /// <summary>The calls that leave the app and wait on a provider. ⚠️ More than one name: the first
    /// version knew only <c>GetResponseAsync</c> and so was blind to the three voice services in its own
    /// scope directory, which reach ElevenLabs and Kokoro over <c>HttpClient.SendAsync</c>. All three
    /// happened to be guarded, so the rule reported a green it had not earned — on the voice path, the
    /// arc that previously shipped an open microphone.</summary>
    private static readonly string[] ProviderCalls =
        ["GetResponseAsync", "GetStreamingResponseAsync", "SendAsync"];

    [Fact]
    public void A_cancellation_caught_at_the_provider_boundary_asks_whose_it_was()
    {
        var bare = new List<string>();
        var seen = 0;

        foreach (var (file, tree) in SourceTree.Of(BoundaryScope, includeRazor: false))
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
        var razorFiles = 0;

        foreach (var (file, tree) in SourceTree.Of(CallScope, includeRazor: true))
        {
            if (file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) razorFiles++;
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax access
                    || !ProviderCalls.Contains(access.Name.Identifier.ValueText)) continue;
                calls++;
                if (!call.Ancestors().OfType<TryStatementSyntax>().Any(x => x.Catches.Any(CanReceiveCancellation)))
                    naked.Add($"{Path.GetFileName(file)}:{Line(call)}");
            }
        }

        Assert.True(calls > 10, $"Only {calls} provider call(s) found — the scan is broken, not the sources.");

        // ⚠️ A REACH guard, not a findings guard, and the two are not the same assertion. No page calls a
        // provider today, so counting provider calls in razor would be zero whether the scan read 65 files
        // or none — which is exactly how the previous version passed while reading none. This asserts the
        // scan got there. If pages stop existing, that is a real change and this should be the thing that
        // says so.
        Assert.True(razorFiles > 20, $"Only {razorFiles} .razor file(s) reached — the razor scan is broken, "
                                   + "not the sources. A scan that reads nothing reports a green it has not earned.");

        Assert.True(naked.Count == 0,
            "A call to the provider is made with no catch that can read a cancellation, so an HttpClient "
            + "timeout escapes the service entirely and reaches whatever UI handler invoked it — where there "
            + "is no ErrorBoundary and the circuit dies with the household's unsaved work in it. Wrap it "
            + "and fail the act, the way every sibling service does:"
            + Environment.NewLine + string.Join(Environment.NewLine, naked));
    }

    /// <summary>
    /// ⚠️ The rule's own classification, tested directly on literal snippets — because both previous
    /// versions of this file were silently wrong in the predicates rather than in the walk, and neither
    /// wrongness could fail anything: a predicate that is too permissive just stops reporting, and the
    /// scan-based facts above go green either way. Each case is one thing the rule has actually been
    /// caught believing, or one it must keep believing. Nothing here touches the sources.
    /// </summary>
    [Theory]
    // The defect itself, in its three disguises — a named type, a bare catch-all rethrow, and a
    // catch-all that singles cancellation out in its filter instead of its declaration.
    [InlineData("catch (OperationCanceledException) { throw; }", false)]
    [InlineData("catch (System.OperationCanceledException) { throw; }", false)]
    [InlineData("catch (TaskCanceledException) { throw; }", false)]
    [InlineData("catch (Exception e) when (e is OperationCanceledException) { throw; }", false)]
    // The inversions. The first was found by hand; the second passed the version that claimed to have
    // closed the first, because it is a binary expression whose left side is a plain read.
    [InlineData("catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw; }", false)]
    [InlineData("catch (OperationCanceledException) when (ct.IsCancellationRequested == false) { throw; }", false)]
    [InlineData("catch (OperationCanceledException) when (ct.IsCancellationRequested != true) { throw; }", false)]
    // A filter that asks some other question is no better than none.
    [InlineData("catch (OperationCanceledException) when (attempt > 2) { throw; }", false)]
    // The rule, and the forms of it that must keep passing.
    [InlineData("catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }", true)]
    [InlineData("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }", true)]
    [InlineData("catch (OperationCanceledException) when ((ct.IsCancellationRequested)) { throw; }", true)]
    [InlineData("catch (OperationCanceledException) when (a.IsCancellationRequested || b.IsCancellationRequested) { throw; }", true)]
    [InlineData("catch (OperationCanceledException) when (ct.IsCancellationRequested && !retried) { throw; }", true)]
    public void The_rule_classifies_a_cancellation_catch_the_way_it_says_it_does(string snippet, bool passes)
    {
        var clause = Parse(snippet);
        Assert.True(DeclaresCancellation(clause), "This snippet is one the 'whose was it?' rule must judge "
                                                + "at all; if it stops declaring cancellation the rule walks past it.");
        Assert.Equal(passes, AsksWhose(clause.Filter));
    }

    /// <summary>⚠️ The wrapping rule's separate question — can this clause RECEIVE a cancellation? A
    /// service that fails the act in a catch-all is guarded; a filtered catch-all is not, because the
    /// filter narrows what reaches it.</summary>
    [Theory]
    [InlineData("catch (Exception ex) { Log(ex); }", true)]
    [InlineData("catch { Log(); }", true)]
    [InlineData("catch (OperationCanceledException) { throw; }", true)]
    [InlineData("catch (TaskCanceledException) { throw; }", true)]
    [InlineData("catch (JsonException ex) { Log(ex); }", false)]
    [InlineData("catch (Exception ex) when (ex is not OperationCanceledException) { Log(ex); }", false)]
    [InlineData("catch (Exception ex) when (ex is HttpRequestException) { Log(ex); }", false)]
    public void The_wrapping_rule_knows_which_catches_can_receive_a_cancellation(string snippet, bool receives) =>
        Assert.Equal(receives, CanReceiveCancellation(Parse(snippet)));

    /// <summary>The one catch clause in a snippet, parsed. Wrapped in just enough C# to be a tree.</summary>
    private static CatchClauseSyntax Parse(string catchClause)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ void M() {{ try {{ }} {catchClause} }} }}");
        Assert.DoesNotContain(tree.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        return tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>().Single();
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
        // `a.IsCancellationRequested || b.IsCancellationRequested` is still asking the question. ⚠️ ONLY
        // `||` and `&&`, because the previous version accepted any binary operator and so let through the
        // exact inversion its own comment said it had closed: `when (ct.IsCancellationRequested == false)`
        // is a binary expression whose left side is a plain read, so it passed. `!ct.IsCancellation…` was
        // rejected only because a prefix `!` is a different node type — the one inversion that had been
        // checked by hand. Anything that is not a plain read joined by `||` or `&&` is reported exactly
        // as an absent filter would be.
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression)
                                        || binary.IsKind(SyntaxKind.LogicalAndExpression)
            => Reads(binary.Left) || Reads(binary.Right),
        _ => false,
    };

    private static int Line(SyntaxNode node) =>
        node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

}
