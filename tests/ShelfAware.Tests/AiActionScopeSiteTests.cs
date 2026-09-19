using System.Text.RegularExpressions;
using ShelfAware.Core.Billing;

namespace ShelfAware.Tests;

/// <summary>
/// Rules about WHERE <see cref="AiActionScope.Begin"/> may appear, held by scanning the source rather than
/// by a paragraph in a doc comment. Both rules below were broken in this repo by code that was written
/// after the paragraph explaining them, and neither failure is visible at runtime: one over-bills silently,
/// the other under-bills silently, and the suite stays green through both.
///
/// <para>⚠️ Lives in the ENGINE suite, not the Web one, although the call sites it scans are in Web and Llm.
/// This is the project Stryker gates: with the test next to the code it guards, a mutant that empties
/// <see cref="CreditPricing.MeteredActions"/> survives — nothing in the mutation-gated suite notices the
/// price list going blank. It needs only Core and the filesystem, so here is where it belongs.</para>
/// </summary>
public class AiActionScopeSiteTests
{
    private static readonly Regex BeginSite =
        new(@"AiActionScope\.Begin\(\s*ServiceAction\.(?<action>\w+)\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// ⚠️ The published price list may quote an action only if something actually charges for it.
    /// <see cref="CreditPricing.MeteredActions"/> is that list, and this is what keeps it from being a
    /// hand-maintained claim about the code: the set and the call sites must agree in BOTH directions.
    ///
    /// <para>It failed in both directions at once when written. <c>TtsSynthesis</c> (3 credits) and
    /// <c>RealtimeMinute</c> (12) were published on the Settings price list and charged by nothing — speech
    /// does not go through the metering layer at all — so a household read a price it could never be
    /// charged. Adding an enum value with a price is one edit; wiring it is another, and nothing connected
    /// the two.</para>
    /// </summary>
    [Fact]
    public void The_published_price_list_quotes_exactly_the_actions_something_charges_for()
    {
        var wired = new HashSet<ServiceAction>();
        var scanned = 0;

        foreach (var file in SourceFiles())
        {
            scanned++;
            foreach (var line in File.ReadAllLines(file).Where(IsCode))
                foreach (Match m in BeginSite.Matches(line))
                    if (Enum.TryParse<ServiceAction>(m.Groups["action"].Value, out var action))
                        wired.Add(action);
        }

        // Without this the test passes vacuously the moment the path walk breaks — green would be what the
        // defect produces, which this suite has been caught by before.
        Assert.True(scanned > 100, $"Only {scanned} .cs file(s) were scanned — the source walk is broken, not the sources.");
        Assert.True(wired.Count > 5, $"Only {wired.Count} Begin site(s) matched — the regex is broken, not the sources.");

        var published = CreditPricing.MeteredActions;
        Assert.True(wired.SetEquals(published),
            "CreditPricing.MeteredActions and the AiActionScope.Begin call sites disagree. "
            + $"Wired but unpublished: [{string.Join(", ", wired.Except(published))}]. "
            + $"Published but unwired: [{string.Join(", ", published.Except(wired))}]. "
            + "An action nothing charges for must not be quoted a price, and an action that IS charged "
            + "must be.");
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
        var offenders = new List<string>();
        var sites = 0;

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsCode(lines[i]) || !BeginSite.IsMatch(lines[i])) continue;
                sites++;
                // Walk back to the nearest line that opens a method body — the first `(`-bearing line above
                // that isn't a comment, an attribute or a continuation. Cheap and sufficient: every site in
                // this repo is a method's own declaration a few lines up.
                if (!EnclosingSignatureIsAsync(lines, i))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} — {lines[i].Trim()}");
            }
        }

        Assert.True(sites > 5, $"Only {sites} Begin site(s) found — the scan is broken, not the sources.");
        Assert.True(offenders.Count == 0,
            "AiActionScope.Begin was called from a method that is not `async`. The scope would escape to the "
            + "caller and never end, and the next unlabelled AI call would be charged nothing:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>A line that is actually code — the doc comments here EXPLAIN the call form, so scanning
    /// them would have the rules match their own explanation.</summary>
    private static bool IsCode(string line)
    {
        var t = line.TrimStart();
        return !t.StartsWith("//") && !t.StartsWith("*");
    }

    private static bool EnclosingSignatureIsAsync(string[] lines, int siteIndex)
    {
        for (var i = siteIndex - 1; i >= 0 && i > siteIndex - 30; i--)
        {
            var line = lines[i].TrimStart();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("///") || line.StartsWith('[')) continue;
            if (line.StartsWith('{') || line.EndsWith(';')) continue;  // a brace-only line, or a statement
            if (!line.Contains('(')) continue;                          // not a signature
            return line.Contains(" async ") || line.StartsWith("async ");
        }
        return false;
    }

    /// <summary>Every <c>.cs</c> under <c>src/</c>, found by walking up to the solution file — so the scan
    /// does not depend on the build's output layout. Build output is excluded: <c>obj/</c> holds generated
    /// copies of the very files being scanned.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShelfAware.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir); // no solution above the test assembly — the walk is wrong, not the sources
        return Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }
}
