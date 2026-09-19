using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ShelfAware.Core.Tagging;

namespace ShelfAware.Tests;

/// <summary>
/// ⚠️ THE rule that "how long may a tag be?" is answered in one place. Only
/// <c>TagVocabulary</c> may compare anything against <see cref="TagVocabulary.MaxLength"/>; everywhere
/// else asks <see cref="TagVocabulary.IsOverLength"/>.
///
/// <para>This is a build rule and not a paragraph because the paragraph was tried, in the very file it
/// was written in. <c>TagVocabulary</c> carried a comment saying two sites had disagreed for one commit
/// — one measuring the raw string, one the trimmed — and that "writing a third arithmetic here would be
/// the same defect again". The commit that added the comment then wrote the arithmetic out four more
/// times: twice more in that file, once in <c>Upload.razor</c>, once in <c>Cookbook.razor</c>, and once
/// in <c>AnthropicTagAdvisor</c> — untrimmed, the one copy that was different. A stored tag of 64
/// characters and a trailing space was a good tag to every site but that one, which dropped it from the
/// advisor's prompt, so the model was never shown the tag it should have matched and coined a duplicate
/// instead.</para>
///
/// <para>⚠️ And the copies were not merely redundant. A seventh site was MISSING rather than wrong:
/// <c>Upload.AddTag</c> had no length check before the paid advisor call, so an over-long tag — which
/// the cap makes the cheap dedup answer "genuinely new" for — escalated to a charged call that
/// interpolated it untruncated. The scattered arithmetic is what made that gap invisible: with the
/// question answered in six places, nobody could see the place it was not being asked.</para>
///
/// <para>CLAUDE.md's item 41 is the general form: the census branch answered "which product does this
/// name mean?" in nine places and fixing them one per round produced three consecutive rounds of new
/// defects. The count only fell when the rule moved into one place. This test is that move, held.</para>
/// </summary>
public class TagLengthSiteTests
{
    /// <summary>Everywhere a tag can be typed, stored, or put in a prompt.</summary>
    private static readonly string[] Scope =
    [
        Path.Combine("src", "ShelfAware.Core"),
        Path.Combine("src", "ShelfAware.Llm"),
        Path.Combine("src", "ShelfAware.Web"),
    ];

    /// <summary>The one file allowed to do the arithmetic, because it is where the answer lives.</summary>
    private const string TheOnePlace = "TagVocabulary.cs";

    [Fact]
    public void Only_TagVocabulary_compares_anything_against_the_tag_length_cap()
    {
        var offenders = new List<string>();
        var reached = 0;

        foreach (var (file, tree) in SourceTree.Of(Scope, includeRazor: true))
        {
            reached++;
            if (Path.GetFileName(file) == TheOnePlace) continue;
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (!IsComparison(node) || !MentionsTheCap(node)) continue;
                offenders.Add($"{Path.GetFileName(file)}:{Line(node)}  {Collapse(node.ToString())}");
            }
        }

        // A reach guard, not a findings guard: zero offenders is the state we want, and is also exactly
        // what a broken walk produces. See ProviderCancellationSiteTests for the version of this rule
        // that shipped scanning nothing under a comment saying it scanned everything.
        Assert.True(reached > 100, $"Only {reached} source file(s) reached — the walk is broken, not the "
                                 + "sources. A scan that reads nothing reports a green it has not earned.");

        Assert.True(offenders.Count == 0,
            "The tag-length cap is compared against outside TagVocabulary. That arithmetic has been "
            + "written seven times in this repo and one copy measured the untrimmed string, so a valid "
            + "tag was a valid tag everywhere but the advisor prompt. Ask TagVocabulary.IsOverLength "
            + "instead — and if a screen needs to say so, TagVocabulary.TooLongMessage:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>⚠️ The rule's own classification, so it cannot quietly stop recognising the thing it
    /// bans. <c>maxlength="@TagVocabulary.MaxLength"</c> in razor MARKUP is not a comparison and is the
    /// correct way for an input to state its own limit; the guard that matters is the server-side one
    /// behind it, and every screen that has the attribute must also ask the predicate.</summary>
    [Theory]
    [InlineData("x = tag.Trim().Length > TagVocabulary.MaxLength;", true)]
    [InlineData("x = e.Length <= TagVocabulary.MaxLength;", true)]
    [InlineData("x = TagVocabulary.MaxLength < n;", true)]
    [InlineData("x = tag.Length is 0 or > MaxLength;", true)]
    [InlineData("x = n == TagVocabulary.MaxLength;", true)]
    [InlineData("x = TagVocabulary.IsOverLength(tag);", false)]
    [InlineData("x = $\"keep it to {TagVocabulary.MaxLength} characters or fewer.\";", false)]
    [InlineData("x = name.Length > 64;", false)]
    public void The_rule_recognises_the_arithmetic_it_bans(string snippet, bool banned)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ void M() {{ var {snippet} }} }}");
        Assert.DoesNotContain(tree.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(banned, tree.GetRoot().DescendantNodes().Any(n => IsComparison(n) && MentionsTheCap(n)));
    }

    /// <summary>A comparison, in either of the two spellings C# has for one.</summary>
    private static bool IsComparison(SyntaxNode node) =>
        node is RelationalPatternSyntax
        || (node is BinaryExpressionSyntax b
            && (b.IsKind(SyntaxKind.GreaterThanExpression) || b.IsKind(SyntaxKind.GreaterThanOrEqualExpression)
                || b.IsKind(SyntaxKind.LessThanExpression) || b.IsKind(SyntaxKind.LessThanOrEqualExpression)
                || b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression)));

    /// <summary>⚠️ The node's OWN text, not its descendants' — a relational pattern's operand sits inside
    /// it, but the subject of an <c>is</c> pattern does not, which is why the whole pattern node is the
    /// thing judged rather than the enclosing expression.</summary>
    private static bool MentionsTheCap(SyntaxNode node) =>
        node.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == nameof(TagVocabulary.MaxLength));

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int Line(SyntaxNode node) =>
        node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
}
