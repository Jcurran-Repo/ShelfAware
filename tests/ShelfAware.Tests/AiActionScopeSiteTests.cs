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
    /// ⚠️ An action is begun with a UNIT COUNT if and only if its price is per unit. Both directions matter
    /// and each one is a silent money defect.
    ///
    /// <para>A count on an action priced per ACT multiplies its charge: a chat turn is one thing the
    /// household asked for however many rounds it took, and five rounds passed as five units would bill it
    /// five times.</para>
    ///
    /// <para>No count on an action priced per UNIT collapses its charge to one unit. Delete
    /// <c>units: setup.SlotCount</c> from the meal plan and every plan — seven meals or a hundred and
    /// twenty-four — costs one credit, while the price list still reads "1 per 3 meals" and the page still
    /// quotes forty-two. Nothing about that is visible on screen or in a green suite, which is the whole
    /// argument for holding it here rather than in a test a fake can sidestep.</para>
    /// </summary>
    [Fact]
    public void An_action_is_begun_with_a_count_exactly_when_it_is_priced_by_the_unit()
    {
        var options = new BillingOptions();
        var wrong = Sites()
            .Where(s => (CreditPricing.UnitsPerPrice(options, s.Action) > 1) != s.HasUnitCount)
            .Select(s => $"{Path.GetFileName(s.File)}:{s.Line} — {s.Action} is priced per "
                       + (s.HasUnitCount ? "act but is begun with a count" : "unit but is begun without one"))
            .ToList();

        Assert.True(wrong.Count == 0,
            "A scope's unit count disagrees with how BillingOptions.UnitsPerPrice prices that action. A "
            + "count on a per-act price multiplies the charge; no count on a per-unit price collapses it to "
            + "one unit:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
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
        var calls = 0;
        foreach (var (file, tree) in Trees())
            foreach (var call in BeginCalls(tree))
            {
                calls++;
                if (ActionOf(call) is null)
                    computed.Add($"{Path.GetFileName(file)}:{Line(call)} — {call}");
            }

        // This rule reports the calls Sites() DROPS, so it cannot run off Sites() and inherit its guard —
        // and a rule whose whole output is "nothing found" is exactly the one that must prove it looked.
        Assert.True(calls > 5, $"Only {calls} Begin call(s) found — the scan is broken, not the sources.");
        Assert.True(computed.Count == 0,
            "AiActionScope.Begin was called with something other than a literal ServiceAction value. The "
            + "price-list and boundary rules in this file read the source, so they cannot see it:"
            + Environment.NewLine + string.Join(Environment.NewLine, computed));
    }

    /// <summary>
    /// ⚠️ Every SURFACE pre-check names the act it is about to run, and names one the price list knows.
    ///
    /// <para>The pre-check and the server-side gate are two halves of one question — "can this household
    /// afford what it is about to do?" — and the whole reason the surface half exists is that the AI
    /// services fail soft, so the gate's refusal never reaches a page. When the two disagree, the household
    /// is waved through by the page and refused by the gate, and what it reads is the surface's generic
    /// "couldn't reach the assistant": the product looks broken to precisely the households closest to
    /// buying more credit.</para>
    ///
    /// <para>They disagreed for exactly one commit. The gate learned to ask an act's real price while the
    /// pre-check kept a defaulted credit count, so thirteen of fourteen sites went on asking whether the
    /// household had ANY credit — and a household holding one credit was waved through for a two-credit
    /// chat turn. The parameter is required now, which is what made the compiler ask every site; this is
    /// what keeps it asked once the compiler is satisfied.</para>
    /// </summary>
    [Fact]
    public void Every_surface_pre_check_names_a_published_action()
    {
        var wrong = new List<string>();
        var calls = 0;

        foreach (var (file, tree) in Trees())
            foreach (var call in CallsTo(tree, "BlockedReasonAsync"))
            {
                calls++;
                // The act is the first ServiceAction-shaped argument, wherever the optional token sits.
                var named = call.ArgumentList.Arguments
                    .Select(a => ActionOfExpression(a.Expression))
                    .FirstOrDefault(a => a is not null);
                if (named is not { } action)
                    wrong.Add($"{Path.GetFileName(file)}:{Line(call)} — names no literal ServiceAction");
                else if (!CreditPricing.MeteredActions.Contains(action))
                    wrong.Add($"{Path.GetFileName(file)}:{Line(call)} — {action} is not on the published price list");
            }

        Assert.True(calls > 10, $"Only {calls} pre-check call(s) found — the scan is broken, not the sources.");
        Assert.True(wrong.Count == 0,
            "A surface pre-check doesn't name the act it is about to run, so it asks a different question "
            + "from the gate that will enforce it — which reads to the household as the assistant being "
            + "broken rather than as a price they can do something about:"
            + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// ⚠️ Every act says what it DELIVERED, so a charge for work that never arrived comes back.
    ///
    /// <para>The charge lands on an act's FIRST provider call, which is what stops two parallel rounds both
    /// paying — so by the time an act knows whether it produced anything, the household's credits have
    /// already moved. <see cref="AiActionScope.Delivered"/> is the correction, and it defaults to NOTHING:
    /// an act that never calls it refunds in full, which is right for the paths that threw and wrong for
    /// every path that worked. So the call has to exist, and this is what says so.</para>
    ///
    /// <para>The rule is deliberately shallow — the call must appear in the same method that opened the
    /// scope. It cannot tell a <c>Delivered(1)</c> on the right branch from one on the wrong branch; that is
    /// each service's own tests. What it does hold is the case that has no symptom at all: a service added
    /// later that charges and never settles, which no screen shows and no green suite catches.</para>
    ///
    /// <para><see cref="AiActionScope.Answered"/> counts, and is what a one-unit act should say: it is
    /// <c>Delivered(Units)</c> under a name that carries the rule for when an AI act has delivered. See
    /// the companion rule below for why a multi-unit act may not use it.</para>
    /// </summary>
    [Fact]
    public void Every_act_reports_what_it_delivered()
    {
        var silent = new List<string>();
        var seen = 0;

        foreach (var (file, tree) in Trees())
            foreach (var call in BeginCalls(tree))
            {
                seen++;
                var owner = EnclosingBody(call);
                if (owner is null) continue; // not in a method body at all — the async rule reports it
                // ⚠️ Pinned to the NAME the scope was bound to, not just any `.Delivered(...)` in the body.
                // An unpinned match is satisfied by some other type's Delivered call sitting in the same
                // method, which would let the act that actually charges go on settling nothing.
                var scope = ScopeNameOf(call);
                var settles = scope is not null && SettleCallsOn(owner, scope).Any();
                if (!settles)
                    silent.Add($"{Path.GetFileName(file)}:{Line(call)} — {ActionOf(call)?.ToString() ?? "?"}");
            }

        // Same non-vacuity guard as the rules above, for the same reason.
        Assert.True(seen > 5, $"Only {seen} Begin site(s) found — the scan is broken, not the sources.");

        Assert.True(silent.Count == 0,
            "An act opens a charging scope and never says what it delivered, so every run of it refunds in "
            + "full — or, if the default is ever flipped, charges for work that never arrived. Call "
            + "Delivered(n) — or Answered() for a one-unit act — on the path that produced something:"
            + Environment.NewLine + string.Join(Environment.NewLine, silent));
    }

    /// <summary>
    /// ⚠️ An act the household pays for BY THE UNIT never settles with <see cref="AiActionScope.Answered"/>.
    ///
    /// <para><c>Answered()</c> is <c>Delivered(Units)</c>: the whole act, because a one-unit act either got
    /// an answer or it didn't. A meal plan is not one unit — the household chose the horizon and pays per
    /// meal — so the same call on a batch that produced three meals out of twelve would claim all twelve
    /// and keep the credits for the nine that never arrived. That act has to count what it persisted.</para>
    ///
    /// <para>Held here rather than inside <see cref="AiActionScope"/> because the alternative is a runtime
    /// throw on the money path, which turns a billing mistake into a failed meal plan for the household
    /// that asked for one. A build that won't compile the mistake costs nobody anything.</para>
    ///
    /// <para>⚠️ It can only see the method that OPENED the scope, so it also refuses to let a per-unit
    /// scope leave that method. Hand the scope to a helper and the helper can call <c>Answered()</c> where
    /// no scan will find it — which is not hypothetical: <c>AnthropicPantryChat</c>'s <c>TurnWrites</c>
    /// takes exactly that shape, correctly, for a one-unit act. A rule that silently stops guarding is
    /// worse than no rule, so the escape is the finding rather than a gap.</para>
    /// </summary>
    [Fact]
    public void An_act_priced_by_the_unit_counts_what_it_delivered()
    {
        var wrong = new List<string>();
        var escaped = new List<string>();
        var byTheUnit = 0;

        foreach (var (file, tree) in Trees())
            foreach (var call in BeginCalls(tree))
            {
                // One unit is the default and needs no argument, so a second argument is the per-unit
                // count — unless it says 1, which is the default written out and settles like any one-unit
                // act. Refusing Answered() there would be the rule crying about correct code.
                if (call.ArgumentList.Arguments.Count < 2) continue;
                if (call.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax { Token.ValueText: "1" })
                    continue;
                byTheUnit++;
                var owner = EnclosingBody(call);
                var scope = ScopeNameOf(call);
                if (owner is null || scope is null) continue; // the rules above report an unbound scope
                var where = $"{Path.GetFileName(file)}:{Line(call)} — {ActionOf(call)?.ToString() ?? "?"}";
                if (SettleCallsOn(owner, scope).Contains(nameof(AiActionScope.Answered)))
                    wrong.Add($"{where} settles with Answered()");
                else if (Escapes(owner, scope))
                    escaped.Add($"{where} hands its scope to something else");
            }

        Assert.True(byTheUnit > 0,
            "No per-unit act found — the scan is broken, not the sources: the meal plan is charged by the "
            + "meal and opens its scope with a units: argument.");

        Assert.True(wrong.Count == 0,
            "An act the household pays for by the unit settles with Answered(), which claims every unit it "
            + "was charged for however few actually arrived — so a plan that produced three meals out of "
            + "twelve keeps the credits for nine it never made. Count what landed with Delivered(n):"
            + Environment.NewLine + string.Join(Environment.NewLine, wrong));

        Assert.True(escaped.Count == 0,
            "A per-unit act passes its scope out of the method that opened it, so the rule above can no "
            + "longer see how it settles and an Answered() in the helper would claim every unit the act "
            + "was charged for. Settle a per-unit act in its own method:"
            + Environment.NewLine + string.Join(Environment.NewLine, escaped));
    }

    /// <summary>Whether the scope bound to <paramref name="scope"/> is handed to anything else in this
    /// body — passed as an argument, or captured into an object. Settling it is a member ACCESS on the
    /// name, which is why those don't count here.</summary>
    private static bool Escapes(SyntaxNode owner, string scope) =>
        owner.DescendantNodes().OfType<ArgumentSyntax>()
            .Any(a => a.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == scope);

    /// <summary>How this body settles the scope bound to <paramref name="scope"/> — one name per call,
    /// empty when it never settles at all.</summary>
    private static IEnumerable<string> SettleCallsOn(SyntaxNode owner, string scope) =>
        owner.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression)
            .OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Expression is IdentifierNameSyntax receiver && receiver.Identifier.ValueText == scope)
            .Select(m => m.Name.Identifier.ValueText)
            .Where(n => n is nameof(AiActionScope.Delivered) or nameof(AiActionScope.Answered));

    /// <summary>
    /// ⚠️ Every scope is opened with <c>await using</c>, so the settlement it holds actually runs.
    ///
    /// <para>Dropping <c>IDisposable</c> from <see cref="AiActionScope"/> made the compiler convert every
    /// site that already said <c>using</c> — that is why it was dropped. It asks nothing at all of a site
    /// written as a plain <c>var action = AiActionScope.Begin(...)</c>, which compiles clean: there is no
    /// Directory.Build.props, no .editorconfig and no analyzer package in this tree, so CA2000 is not
    /// enforced and nothing would say a word.</para>
    ///
    /// <para>The cost of that is worse than a missed refund. The scope is never disposed, so the ambient
    /// one is never restored: every later AI call on that flow finds a stale scope whose one charge is
    /// already claimed and is therefore FREE, and the charge that did land is never settled. One forgotten
    /// keyword silently stops charging for a whole code path.</para>
    /// </summary>
    [Fact]
    public void Every_scope_is_opened_with_await_using()
    {
        var loose = new List<string>();
        var seen = 0;

        foreach (var (file, tree) in Trees())
            foreach (var call in BeginCalls(tree))
            {
                seen++;
                // ⚠️ The DECLARATION that holds this call, not any ancestor. `Ancestors().Any(...)` is the
                // obvious spelling and it is wrong: a Begin sitting loose inside an unrelated
                // `await using (var db = ...) { ... }` block has that block as an ancestor and would pass,
                // which is precisely the leak this rule exists to catch.
                var declarator = call.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
                var declared = declarator?.Initializer?.Value == call
                    && declarator!.Parent?.Parent switch
                    {
                        // `await using var action = Begin(...);`
                        LocalDeclarationStatementSyntax
                            { UsingKeyword.RawKind: not 0, AwaitKeyword.RawKind: not 0 } => true,
                        // `await using (var action = Begin(...)) { ... }` — a different Roslyn shape, and
                        // perfectly good code. Leaving it out made this rule fail the build on a site that
                        // was correctly disposed AND could settle, which is the worse kind of wrong: a rule
                        // that cries about good code gets weakened or deleted, and then it holds nothing.
                        UsingStatementSyntax { AwaitKeyword.RawKind: not 0 } => true,
                        _ => false,
                    };
                // …and the nameless block form, `await using (Begin(...))`. It IS disposed, so it is not
                // this rule's finding — it settles nothing, which Every_act_reports_what_it_delivered says.
                var held = declared || call.Parent is UsingStatementSyntax { AwaitKeyword.RawKind: not 0 };
                if (!held) loose.Add($"{Path.GetFileName(file)}:{Line(call)} — {ActionOf(call)?.ToString() ?? "?"}");
            }

        // Without this the rule passes vacuously the moment the walk or the parse breaks — green would be
        // what the defect produces, which is the trap this suite has been caught by before.
        Assert.True(seen > 5, $"Only {seen} Begin site(s) found — the scan is broken, not the sources.");

        Assert.True(loose.Count == 0,
            "An act opens a charging scope without `await using`, so it is never disposed: the settlement "
            + "never runs, and — worse — the ambient scope is never restored, which makes every later AI "
            + "call on that path ride the stale scope's spent charge for free:"
            + Environment.NewLine + string.Join(Environment.NewLine, loose));
    }

    // ------------------------------------------------------------------ the scan

    /// <summary>The identifier a Begin call was bound to — <c>action</c> in
    /// <c>await using var action = AiActionScope.Begin(...)</c> — or null when it was bound to nothing
    /// nameable (an <c>await using (AiActionScope.Begin(...))</c> block, which cannot settle anyway since
    /// there is no name to call Delivered on).</summary>
    private static string? ScopeNameOf(InvocationExpressionSyntax call) =>
        call.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.ValueText;


    private sealed record Site(ServiceAction Action, string File, int Line, bool InAsyncMethod, bool HasUnitCount);

    private static List<Site> Sites()
    {
        var sites = new List<Site>();
        var files = 0;

        foreach (var (file, tree) in Trees())
        {
            files++;
            foreach (var call in BeginCalls(tree))
            {
                if (ActionOf(call) is not { } action) continue; // reported by No_scope_is_begun_from_a_computed_action
                sites.Add(new Site(action, file, Line(call), IsInsideAsync(call), call.ArgumentList.Arguments.Count >= 2));
            }
        }

        // Without these the rules pass vacuously the moment the walk or the parse breaks — green would be
        // what the defect produces, which is the trap this suite has been caught by before.
        Assert.True(files > 100, $"Only {files} source file(s) parsed — the scan is broken, not the sources.");
        Assert.True(sites.Count > 5, $"Only {sites.Count} Begin site(s) found — the scan is broken, not the sources.");
        return sites;
    }

    /// <summary>The literal <see cref="ServiceAction"/> a Begin call names, or null when it names something
    /// computed. Only the FIRST argument is the action — the second, when present, is how many of the
    /// action's units the act covers.</summary>
    private static ServiceAction? ActionOf(InvocationExpressionSyntax call) =>
        ActionOfExpression(call.ArgumentList.Arguments.FirstOrDefault()?.Expression);

    /// <summary>The literal <see cref="ServiceAction"/> an expression names, or null for anything else —
    /// a variable, a computed value, or an argument that isn't an action at all.</summary>
    private static ServiceAction? ActionOfExpression(ExpressionSyntax? expression) =>
        expression is MemberAccessExpressionSyntax
            { Expression: IdentifierNameSyntax { Identifier.ValueText: nameof(ServiceAction) }, Name.Identifier.ValueText: var name }
        && Enum.TryParse<ServiceAction>(name, out var action)
            ? action
            : null;

    private static IEnumerable<InvocationExpressionSyntax> BeginCalls(SyntaxTree tree) =>
        CallsTo(tree, nameof(AiActionScope.Begin))
            .Where(i => ((MemberAccessExpressionSyntax)i.Expression).Expression
                is IdentifierNameSyntax { Identifier.ValueText: nameof(AiActionScope) });

    private static IEnumerable<InvocationExpressionSyntax> CallsTo(SyntaxTree tree, string method) =>
        tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.ValueText == method);

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>The method, local function or lambda a call sits in — the body a scope's whole life is
    /// spent inside, so the body its settlement has to appear in.</summary>
    private static SyntaxNode? EnclosingBody(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
            if (n is MethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
                return n;
        return null;
    }

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
