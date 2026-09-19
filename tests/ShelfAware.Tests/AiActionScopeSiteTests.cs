using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ShelfAware.Core.Billing;

namespace ShelfAware.Tests;

/// <summary>
/// Rules about WHERE <see cref="AiActionScope.Begin"/> may appear, held by PARSING the source rather than by
/// a paragraph in a doc comment. Every rule here failed in real code first, and none of the failures is
/// visible at runtime: one over-bills silently, the others under-bill silently, and a full suite stays green
/// through all of them.
///
/// <para>⚠️ Lives in the ENGINE suite, not the Web one, although the call sites it scans are in Web and Llm.
/// This is the project Stryker gates: with the test next to the code it guards, a mutant that empties
/// <see cref="CreditPricing.MeteredActions"/> survives — nothing in the mutation-gated suite notices the
/// published price list going blank. It needs only Core and the filesystem, so here is where it belongs.</para>
///
/// <para>⚠️ Roslyn, not a regex over lines. The first version of this file walked backwards from a call site
/// looking for a line that "looked like" an async signature, and a review found it both missed the leak it
/// existed to catch (an expression-bodied member, where the walk sails past the whole previous method body
/// and reads THAT signature) and flagged correct code (any guard clause containing a parenthesis). A rule
/// worth holding is worth parsing for.</para>
/// </summary>
public class AiActionScopeSiteTests
{
    /// <summary>How many distinct FILES may own each action's charging boundary. Almost always one — an act
    /// has one place it begins. Anything else is written down here with its reason, so adding a second
    /// boundary is a decision somebody makes rather than a line somebody adds.</summary>
    private static readonly Dictionary<ServiceAction, int> BoundaryFiles = new()
    {
        [ServiceAction.ReceiptExtraction] = 1,
        [ServiceAction.CensusPhoto] = 1,
        [ServiceAction.ChatTurn] = 1,
        [ServiceAction.RecipeSuggest] = 1,
        [ServiceAction.RecipeAdapt] = 1,
        [ServiceAction.RecipeImport] = 1,
        [ServiceAction.MealPlan] = 1,
        [ServiceAction.MealReroll] = 1,
        // Two advisors suggest tags — one for products, one for recipes — and they are separate services
        // with separate prompts. Both acts are "suggesting tags" to the household, and the price is 0
        // anyway, so they share the action.
        [ServiceAction.TagSuggest] = 2,
        [ServiceAction.SubstituteSuggest] = 1,
        [ServiceAction.IngredientAlternatives] = 1,
    };

    /// <summary>
    /// ⚠️ The published price list may quote an action only if something actually charges for it, and
    /// everything that charges must be quoted. <see cref="CreditPricing.MeteredActions"/> is that list; this
    /// is what keeps it from being a hand-maintained claim ABOUT the code rather than a fact OF it.
    ///
    /// <para>It failed in both directions at once when written. <c>TtsSynthesis</c> (3 credits) and
    /// <c>RealtimeMinute</c> (12) were published on the Settings price list and charged by nothing — speech
    /// never enters the metering layer — so a household read a price it could never be charged. Adding an
    /// enum value with a price is one edit; wiring it is another, and nothing connected the two.</para>
    /// </summary>
    [Fact]
    public void The_published_price_list_quotes_exactly_the_actions_something_charges_for()
    {
        var sites = Sites();
        var wired = sites.Select(s => s.Action).ToHashSet();
        var published = CreditPricing.MeteredActions;

        Assert.True(wired.SetEquals(published),
            "CreditPricing.MeteredActions and the AiActionScope.Begin call sites disagree. "
            + $"Wired but unpublished: [{string.Join(", ", wired.Except(published))}]. "
            + $"Published but unwired: [{string.Join(", ", published.Except(wired))}]. "
            + "An action nothing charges for must not be quoted a price, and an action that IS charged must be.");
    }

    /// <summary>
    /// ⚠️ One act, one place it begins. This is the guard on the most expensive defect this code has had:
    /// the meal-plan scope sat inside the generator, which runs once per seven-slot BATCH, so a 31-day plan
    /// opened eighteen scopes and took eighteen charges for a thing the price list quotes at two.
    ///
    /// <para>Moving the boundary fixed that instance. This is what stops it coming back — and it is needed
    /// because the obvious test cannot see it: <c>MealPlanServiceTests</c> drives a FAKE generator, so a
    /// scope re-added inside the real one is structurally invisible to it, and the set-equality rule above
    /// would not notice either, because a second site for an action already in the set changes no set.</para>
    /// </summary>
    [Fact]
    public void Each_action_has_exactly_one_place_its_charge_begins()
    {
        var actual = Sites()
            .GroupBy(s => s.Action)
            .ToDictionary(g => g.Key, g => g.Select(s => s.File).Distinct().Count());

        var wrong = actual
            .Where(kv => !BoundaryFiles.TryGetValue(kv.Key, out var allowed) || allowed != kv.Value)
            .Select(kv => $"{kv.Key}: begun in {kv.Value} file(s), expected {(BoundaryFiles.TryGetValue(kv.Key, out var a) ? a.ToString() : "none — it is not in the map")}"
                          + $" — {string.Join(", ", Sites().Where(s => s.Action == kv.Key).Select(s => Path.GetFileName(s.File)).Distinct())}")
            .ToList();

        Assert.True(wrong.Count == 0,
            "An action's charging boundary moved or multiplied. A second scope for the same action means a "
            + "second charge for one act — which is how a meal plan came to be billed eighteen times. If the "
            + "new site is deliberate, say so in BoundaryFiles with its reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// ⚠️ <see cref="AiActionScope.Begin"/> must be called from an <c>async</c> method. The scope lives in
    /// the <see cref="System.Threading.ExecutionContext"/>, and an async method's synchronous prologue has
    /// its context RESTORED when it returns — which is the only reason the scope cannot escape upward into
    /// the Blazor circuit and be picked up by a later, unrelated call. A plain method returning a
    /// <c>Task</c> has no such prologue: the scope leaks to the caller and never ends, and the next
    /// unlabelled AI call finds that stale scope's charge already claimed and is FREE.
    ///
    /// <para>Every site is async today, so this holds a property the code already has — which is the point.
    /// The failure it prevents is invisible: nothing throws, nothing logs, a suite stays green, and
    /// households stop being billed.</para>
    /// </summary>
    [Fact]
    public void Every_scope_is_begun_from_an_async_method()
    {
        var offenders = Sites()
            .Where(s => !s.InAsyncMethod)
            .Select(s => $"{Path.GetFileName(s.File)}:{s.Line}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "AiActionScope.Begin was called from a method that is not `async`. The scope would escape to the "
            + "caller and never end, and the next unlabelled AI call would be charged nothing:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// ⚠️ The action must be a literal <c>ServiceAction.X</c>, not a variable. The two rules above read the
    /// source, so a computed action is one this file cannot see: it would be charged without ever being
    /// published, and its boundary would never be counted. Nothing computes one today; this is what keeps
    /// the scan's silence meaningful rather than merely quiet.
    /// </summary>
    [Fact]
    public void No_scope_is_begun_from_a_computed_action()
    {
        var computed = new List<string>();
        foreach (var (file, tree) in Trees())
            foreach (var call in BeginCalls(tree))
                if (call.ArgumentList.Arguments is not [{ Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: nameof(ServiceAction) } } }])
                    computed.Add($"{Path.GetFileName(file)}:{Line(call)} — {call}");

        Assert.True(computed.Count == 0,
            "AiActionScope.Begin was called with something other than a literal ServiceAction value. The "
            + "price-list and boundary rules in this file read the source, so they cannot see it:"
            + Environment.NewLine + string.Join(Environment.NewLine, computed));
    }

    // ------------------------------------------------------------------ the scan

    private sealed record Site(ServiceAction Action, string File, int Line, bool InAsyncMethod);

    private static List<Site> Sites()
    {
        var sites = new List<Site>();
        var files = 0;

        foreach (var (file, tree) in Trees())
        {
            files++;
            foreach (var call in BeginCalls(tree))
            {
                if (call.ArgumentList.Arguments is not [{ Expression: MemberAccessExpressionSyntax
                    { Expression: IdentifierNameSyntax { Identifier.ValueText: nameof(ServiceAction) }, Name.Identifier.ValueText: var name } }])
                    continue; // reported by No_scope_is_begun_from_a_computed_action
                if (!Enum.TryParse<ServiceAction>(name, out var action)) continue;
                sites.Add(new Site(action, file, Line(call), IsInsideAsync(call)));
            }
        }

        // Without these the rules pass vacuously the moment the walk or the parse breaks — green would be
        // what the defect produces, which is the trap this suite has been caught by before.
        Assert.True(files > 100, $"Only {files} source file(s) parsed — the scan is broken, not the sources.");
        Assert.True(sites.Count > 5, $"Only {sites.Count} Begin site(s) found — the scan is broken, not the sources.");
        return sites;
    }

    private static IEnumerable<InvocationExpressionSyntax> BeginCalls(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: nameof(AiActionScope.Begin),
                Expression: IdentifierNameSyntax { Identifier.ValueText: nameof(AiActionScope) },
            });

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>The nearest enclosing thing that has its own <c>ExecutionContext</c> prologue — a method, a
    /// local function, or a lambda — and whether it is <c>async</c>. Exact, where the old line walk guessed.</summary>
    private static bool IsInsideAsync(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case MethodDeclarationSyntax m: return m.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case LocalFunctionStatementSyntax f: return f.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case AnonymousFunctionExpressionSyntax a: return a.AsyncKeyword != default;
                case AccessorDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax:
                    return false; // none of these can be async — a scope begun here leaks
            }
        }
        return false;
    }

    /// <summary>Every C# source under <c>src/</c>, parsed. <c>.razor</c> counts: the repo's own plan says
    /// those files still hold logic (D1), so a charge begun in an <c>@code</c> block is exactly the door a
    /// <c>*.cs</c>-only scan would leave open. Their <c>@code</c> blocks are lifted out and parsed as a class
    /// body, which is what they are.</summary>
    private static IEnumerable<(string File, SyntaxTree Tree)> Trees()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShelfAware.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir); // no solution above the test assembly — the walk is wrong, not the sources

        var src = Path.Combine(dir!.FullName, "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            yield return (file, CSharpSyntaxTree.ParseText(File.ReadAllText(file)));
        }

        foreach (var file in Directory.EnumerateFiles(src, "*.razor", SearchOption.AllDirectories))
        {
            if (CodeBlockOf(File.ReadAllText(file)) is { } code)
                yield return (file, CSharpSyntaxTree.ParseText($"class RazorCodeBlock {{{code}}}"));
        }
    }

    /// <summary>The body of a <c>.razor</c> file's <c>@code { … }</c> block, by brace matching, or null when
    /// the file has none. Crude on purpose: it only has to find C# that would otherwise be invisible, and a
    /// file whose block it misreads fails to parse rather than passing quietly.</summary>
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
