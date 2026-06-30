using System.Text.Json;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Evaluation;
using ShelfAware.Core.Extraction;
using ShelfAware.Llm;

// Extraction-accuracy harness (DESIGN.md §9). Runs IReceiptExtractor over labelled
// fixtures and scores line recall/precision + field accuracy, then writes a JSON the
// Accuracy page renders.
//
//   dotnet run --project tests/ShelfAware.Evals -- [fixturesDir] [outPath]
//
// API key: set the Llm__ApiKey (or ANTHROPIC_API_KEY) environment variable.
// Fixtures: a directory of "<name>.expected.json" (ground truth) next to a
// "<name>.<png|jpg|jpeg|webp|pdf>" receipt image.

var fixturesDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "fixtures");
var outPath = args.Length > 1 ? args[1] : "eval-results.json";
// Set EVAL_VERBOSE=1 to print, per fixture, which expected/found lines didn't pair — useful for
// telling real extraction misses apart from name-normalization differences, and for noting wobble.
var verbose = Environment.GetEnvironmentVariable("EVAL_VERBOSE") == "1";

var apiKey = Environment.GetEnvironmentVariable("Llm__ApiKey")
    ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set Llm__ApiKey (or ANTHROPIC_API_KEY) to run the eval harness.");
    return 1;
}

if (!Directory.Exists(fixturesDir))
{
    Console.Error.WriteLine($"No fixtures directory at {fixturesDir}.");
    Console.Error.WriteLine("Add <name>.<png|jpg|webp|pdf> images alongside <name>.expected.json files.");
    return 1;
}

var model = new LlmOptions().ExtractionModel;
IReceiptExtractor extractor = new AnthropicReceiptExtractor(Options.Create(new LlmOptions { ApiKey = apiKey }));

var expectedFiles = Directory.GetFiles(fixturesDir, "*.expected.json").OrderBy(f => f).ToList();
if (expectedFiles.Count == 0)
{
    Console.Error.WriteLine($"No *.expected.json fixtures found in {fixturesDir}.");
    return 1;
}

var scores = new List<FixtureScore>();
foreach (var expectedFile in expectedFiles)
{
    var name = Path.GetFileName(expectedFile).Replace(".expected.json", "");
    var imagePath = FindImage(fixturesDir, name);
    if (imagePath is null)
    {
        scores.Add(new FixtureScore { Name = name, Error = "no image found" });
        continue;
    }

    var expected = JsonSerializer.Deserialize<List<ExpectedLine>>(File.ReadAllText(expectedFile)) ?? [];
    var attachment = new ReceiptAttachment(File.ReadAllBytes(imagePath), MediaTypeFor(imagePath));

    var result = await extractor.ExtractAsync([attachment]);
    if (!result.Success || result.Receipt is null)
    {
        scores.Add(new FixtureScore { Name = name, ExpectedLines = expected.Count, Error = result.Error });
        continue;
    }

    var (matched, fieldHits, pairs, missExpected, missFound) = Evaluate(expected, result.Receipt.Lines);
    scores.Add(BuildScore(name, expected.Count, result.Receipt.Lines.Count, matched, fieldHits));
    if (verbose)
    {
        Console.WriteLine($"\n[{name}] matched pairs (expected ↔ found):");
        foreach (var p in pairs) Console.WriteLine($"  {p}");
        foreach (var e in missExpected) Console.WriteLine($"  expected · NOT FOUND → {e}");
        foreach (var f in missFound) Console.WriteLine($"  found · NOT EXPECTED → {f}");
    }
}

var aggregate = new EvalAggregate
{
    Recall = Mean(scores, s => s.Recall),
    Precision = Mean(scores, s => s.Precision),
    FieldAccuracy = Mean(scores, s => s.FieldAccuracy),
};

var results = new EvalResults
{
    GeneratedAt = DateTimeOffset.Now,
    Model = model,
    Aggregate = aggregate,
    Fixtures = scores,
};

PrintTable(scores, aggregate);
File.WriteAllText(outPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nWrote {Path.GetFullPath(outPath)}");
return 0;

static string? FindImage(string dir, string name) =>
    new[] { "png", "jpg", "jpeg", "webp", "gif", "pdf" }
        .Select(ext => Path.Combine(dir, $"{name}.{ext}"))
        .FirstOrDefault(File.Exists);

static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".png" => "image/png",
    ".webp" => "image/webp",
    ".gif" => "image/gif",
    ".pdf" => "application/pdf",
    _ => "image/jpeg",
};

// Greedy fuzzy match each expected line to a found line by normalized-name similarity (≥ 0.8), then
// score quantity + category on the matched pairs. Returns the unmatched names on both sides for the
// verbose diagnostic.
static (int matched, int fieldHits, List<string> pairs, List<string> missExpected, List<string> missFound) Evaluate(
    List<ExpectedLine> expected, IReadOnlyList<ExtractedLine> found)
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
    return (matched, fieldHits, pairs, missExpected, missFound);
}

static FixtureScore BuildScore(string name, int expectedCount, int foundCount, int matched, int fieldHits) =>
    new()
    {
        Name = name,
        ExpectedLines = expectedCount,
        FoundLines = foundCount,
        MatchedLines = matched,
        Recall = expectedCount == 0 ? 1 : (double)matched / expectedCount,
        Precision = foundCount == 0 ? (expectedCount == 0 ? 1 : 0) : (double)matched / foundCount,
        FieldAccuracy = matched == 0 ? 0 : (double)fieldHits / matched,
    };

static double TokenSimilarity(string a, string b)
{
    var ta = Tokens(a);
    var tb = Tokens(b);
    if (ta.Count == 0 || tb.Count == 0) return 0;
    var inter = ta.Count(tb.Contains);
    // Overlap (containment) coefficient: |A ∩ B| / min(|A|, |B|). For product names a concise canonical
    // label ("Lean Ground Beef") and a verbose extraction ("All Natural 93% Lean Ground Beef") are the
    // same item but differ in descriptor words; symmetric Jaccard wrongly penalizes that. Containment
    // asks "is the shorter name largely inside the longer", which is the right question for same-product.
    return (double)inter / Math.Min(ta.Count, tb.Count);
}

static HashSet<string> Tokens(string s) =>
    new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet();

static double Mean(List<FixtureScore> scores, Func<FixtureScore, double> selector)
{
    var scored = scores.Where(s => s.Error is null).ToList();
    return scored.Count == 0 ? 0 : scored.Average(selector);
}

static void PrintTable(List<FixtureScore> scores, EvalAggregate agg)
{
    Console.WriteLine($"{"Fixture",-28} {"Exp",4} {"Found",6} {"Recall",8} {"Prec",8} {"Field",8}");
    Console.WriteLine(new string('-', 70));
    foreach (var s in scores)
    {
        if (s.Error is not null)
        {
            Console.WriteLine($"{s.Name,-28} ERROR: {s.Error}");
            continue;
        }
        Console.WriteLine($"{s.Name,-28} {s.ExpectedLines,4} {s.FoundLines,6} {s.Recall,8:P0} {s.Precision,8:P0} {s.FieldAccuracy,8:P0}");
    }
    Console.WriteLine(new string('-', 70));
    Console.WriteLine($"{"AGGREGATE",-28} {"",4} {"",6} {agg.Recall,8:P0} {agg.Precision,8:P0} {agg.FieldAccuracy,8:P0}");
    Console.WriteLine($"Targets: recall ≥ 90%, precision ≥ 90%, field accuracy ≥ 85%.");
}

record ExpectedLine
{
    public string NormalizedName { get; init; } = "";
    public decimal Quantity { get; init; } = 1;
    public string Category { get; init; } = "Other";
}
