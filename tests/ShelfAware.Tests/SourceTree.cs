using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ShelfAware.Tests;

/// <summary>
/// The source walk the build rules share: given a scope of project directories and named files, hand
/// back a parsed tree per file, lifting a <c>.razor</c> file's <c>@code</c> block so C# that would
/// otherwise be invisible is judged too.
///
/// <para>⚠️ ONE copy, because the rules that use it are themselves about not writing a rule twice. The
/// second rule to need a razor-aware walk would have copied this one, and the copy would have been the
/// version without the obj/bin filter, or without the parse-diagnostic assertion — the same shape as the
/// seven copies of the tag-length arithmetic that <see cref="TagLengthSiteTests"/> exists to stop.</para>
/// </summary>
internal static class SourceTree
{
    /// <summary>The four pages whose <c>@code</c> block is not, and cannot be, C#: each defines a
    /// <c>RenderFragment</c> with a razor TEMPLATE expression (<c>=&gt; @&lt;div&gt;…</c>), which the
    /// razor compiler turns into C# but a plain lift cannot.
    ///
    /// <para>⚠️ Named, and asserted to be exactly this set by
    /// <see cref="SourceTreeTests.The_only_unliftable_pages_are_the_ones_named_here"/>, rather than
    /// skipped quietly. A file the lift cannot read is a file the rules do not judge, and the whole
    /// argument of these rules is that an unjudged file has to be visible: the previous razor scan
    /// silently read nothing at all under a comment saying it read everything. So a fifth page growing a
    /// template expression fails the build and has to be looked at, rather than dropping out of the scan
    /// the way these four would have.</para>
    ///
    /// <para>What the gap costs today: none of the four calls a provider, and their cancellation catches
    /// sit outside the boundary scope the "whose was it?" rule judges. If one ever needs judging, the
    /// answer is to move the fragment into a component rather than to widen the lift.</para></summary>
    internal static readonly string[] Unliftable =
        ["MainLayout.razor", "Accuracy.razor", "GroceryList.razor", "MealPlanPage.razor"];

    internal static IEnumerable<(string File, SyntaxTree Tree)> Of(string[] scope, bool includeRazor)
    {
        var dir = RepoTree.Root();

        foreach (var entry in scope)
        {
            var path = Path.Combine(dir.FullName, entry);
            var files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                : [path];
            foreach (var file in files)
            {
                if (Generated(file)) continue;
                Assert.True(File.Exists(file), $"{entry} is not on disk — the scope list is stale, so the "
                                             + "rule is silently guarding less than it claims.");
                yield return (file, CSharpSyntaxTree.ParseText(File.ReadAllText(file)));
            }

            // ⚠️ .razor too. The bare rethrow that actually tore a circuit lived in an @code block, and
            // the first version of this scan globbed *.cs, so it could not see the file it most needed to.
            if (!includeRazor || !Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*.razor", SearchOption.AllDirectories))
            {
                if (Generated(file)) continue; // ⚠️ the .cs walk filtered these and the razor walk did not
                if (CodeBlockOf(File.ReadAllText(file)) is not { } code) continue;
                var tree = CSharpSyntaxTree.ParseText($"class RazorCodeBlock {{{code}}}");
                // ⚠️ Roslyn does NOT throw on malformed input — it error-recovers and hands back a partial
                // tree. So a block whose braces CodeBlockOf misreads (a "{" inside a string literal, say)
                // would be walked for too few nodes and the rule would silently guard less, which is the
                // one failure this file exists to make impossible. Asserted rather than assumed: the
                // comment here used to claim the parse would fail, and it never would have.
                // ⚠️ The message is built only when it is needed. Written inline it read
                // `broken[0].GetMessage()`, which C# evaluates before Assert.True is called — so the
                // assertion that exists to catch a silent gap threw IndexOutOfRange on every healthy file
                // instead. A guard that fails on the good case is not a guard.
                var broken = tree.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
                if (broken is not null && Unliftable.Contains(Path.GetFileName(file))) continue;
                if (broken is not null)
                    Assert.Fail($"The @code block lifted from {Path.GetFileName(file)} does not parse, so the "
                              + $"rule is walking a partial tree and guarding less than it claims: "
                              + $"{broken.GetMessage()} at lifted line {broken.Location.GetLineSpan().StartLinePosition.Line + 1}: "
                              + LiftedLine(code, broken.Location.GetLineSpan().StartLinePosition.Line));
                yield return (file, tree);
            }
        }
    }

    private static bool Generated(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    /// <summary>The body of a <c>.razor</c> file's <c>@code { … }</c> block, by brace matching, or null
    /// when the file has none. Crude on purpose: it only has to find C# that would otherwise be invisible.
    /// <para>⚠️ It does NOT report its own failure — this doc used to claim "a file whose block it
    /// misreads fails to parse rather than passing quietly", and that was never true. Roslyn error-recovers
    /// instead of throwing, so a misread block yields a partial tree that the rules walk for too few nodes
    /// and pass. The caller checks the diagnostics; that check is what makes this crude lift safe to
    /// use, and it is what found the four template-expression pages in <see cref="Unliftable"/>.</para></summary>
    internal static string? CodeBlockOf(string razor)
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

    /// <summary>The offending line of a lifted block, for an error message that can actually be acted on.
    /// ⚠️ Line 0 of the lifted text is the synthetic wrapper, so the block's own lines are offset by one.</summary>
    private static string LiftedLine(string code, int zeroBased)
    {
        var lines = code.Split('\n');
        var i = zeroBased - 1; // the wrapper occupies the first line
        return i >= 0 && i < lines.Length ? lines[i].Trim() : "(past the end of the lifted block)";
    }
}
