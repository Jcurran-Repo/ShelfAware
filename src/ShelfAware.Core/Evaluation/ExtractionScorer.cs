using ShelfAware.Core.Extraction;

namespace ShelfAware.Core.Evaluation;

/// <summary>One ground-truth receipt line: the canonical item name plus the two scored fields.</summary>
public record ExpectedLine
{
    public string NormalizedName { get; init; } = "";
    public decimal Quantity { get; init; } = 1;
    public string Category { get; init; } = "Other";
}

/// <summary>What one scoring pass found, including the diagnostic pairings for a verbose report.</summary>
public record ScoreDetail(
    int Matched,
    int FieldHits,
    IReadOnlyList<string> Pairs,
    IReadOnlyList<string> MissingExpected,
    IReadOnlyList<string> Unexpected);

/// <summary>
/// Scores extraction output against hand-verified ground truth (DESIGN.md §9) — THE scoring math,
/// shared by the offline eval harness (tests/ShelfAware.Evals) and the in-app "your receipts"
/// accuracy check so the two can never disagree about what "accurate" means. Names match by the
/// token containment coefficient (|A∩B| / min(|A|,|B|) ≥ 0.6): a concise canonical label ("Lean
/// Ground Beef") and a verbose extraction ("All Natural 93% Lean Ground Beef") are the same item,
/// which symmetric Jaccard wrongly penalized. Tokens fold bare plurals — extraction wobbles
/// between "Lime" and "Limes" run to run, and that must not score as a missed line.
/// </summary>
public static class ExtractionScorer
{
    /// <summary>Greedy-match each expected line to its most similar unused found line (≥ 0.6), then
    /// score quantity + category on the matched pairs. Duplicate expected lines (a real receipt can
    /// list one item twice) each consume a distinct found line.</summary>
    public static ScoreDetail Score(IReadOnlyList<ExpectedLine> expected, IReadOnlyList<ExtractedLine> found)
    {
        var usedFound = new bool[found.Count];
        var matched = 0;
        var fieldHits = 0;
        var pairs = new List<string>();
        var missExpected = new List<string>();
        foreach (var exp in expected)
        {
            var best = -1;
            var bestSim = 0.0;
            for (var i = 0; i < found.Count; i++)
            {
                if (usedFound[i]) continue;
                var sim = TokenSimilarity(exp.NormalizedName, found[i].NormalizedName);
                if (sim > bestSim) { bestSim = sim; best = i; }
            }
            if (best >= 0 && bestSim >= 0.6)
            {
                usedFound[best] = true;
                matched++;
                var f = found[best];
                var qtyOk = Math.Abs(f.Quantity - exp.Quantity) <= 0.01m;
                var catOk = string.Equals(f.Category.ToString(), exp.Category, StringComparison.OrdinalIgnoreCase);
                if (qtyOk && catOk) fieldHits++;
                var flags = (qtyOk ? "" : " [qty]") + (catOk ? "" : $" [cat: exp {exp.Category} ≠ {f.Category}]");
                pairs.Add($"{exp.NormalizedName}  ↔  {f.NormalizedName}{flags}");
            }
            else missExpected.Add(exp.NormalizedName);
        }
        var missFound = new List<string>();
        for (var i = 0; i < found.Count; i++)
            if (!usedFound[i]) missFound.Add(found[i].NormalizedName);
        return new ScoreDetail(matched, fieldHits, pairs, missExpected, missFound);
    }

    /// <summary>Fold a score into the shape the Accuracy page renders.</summary>
    public static FixtureScore ToFixtureScore(string name, int expectedCount, int foundCount, ScoreDetail detail) =>
        new()
        {
            Name = name,
            ExpectedLines = expectedCount,
            FoundLines = foundCount,
            MatchedLines = detail.Matched,
            Recall = expectedCount == 0 ? 1 : (double)detail.Matched / expectedCount,
            Precision = foundCount == 0 ? (expectedCount == 0 ? 1 : 0) : (double)detail.Matched / foundCount,
            FieldAccuracy = detail.Matched == 0 ? 0 : (double)detail.FieldHits / detail.Matched,
        };

    /// <summary>Mean of the error-free per-fixture scores — the aggregate line on the Accuracy page.</summary>
    public static EvalAggregate Aggregate(IReadOnlyList<FixtureScore> scores)
    {
        var scored = scores.Where(s => s.Error is null).ToList();
        return scored.Count == 0
            ? new EvalAggregate()
            : new EvalAggregate
            {
                Recall = scored.Average(s => s.Recall),
                Precision = scored.Average(s => s.Precision),
                FieldAccuracy = scored.Average(s => s.FieldAccuracy),
            };
    }

    public static double TokenSimilarity(string a, string b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var inter = ta.Count(tb.Contains);
        return (double)inter / Math.Min(ta.Count, tb.Count);
    }

    private static HashSet<string> Tokens(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Singular)
            .ToHashSet();

    // Fold a simple trailing-s plural (not -ss); both sides get the same folding, so it can't
    // manufacture similarity that isn't there.
    private static string Singular(string token) =>
        token.Length >= 4 && token.EndsWith('s') && !token.EndsWith("ss") ? token[..^1] : token;
}
