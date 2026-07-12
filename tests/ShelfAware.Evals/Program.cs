using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
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
var chatClient = new AnthropicClient { ApiKey = apiKey }.AsIChatClient(model);
IReceiptExtractor extractor = new AnthropicReceiptExtractor(
    chatClient,
    Options.Create(new LlmOptions { ApiKey = apiKey }),
    NullLogger<AnthropicReceiptExtractor>.Instance);

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

    var detail = ExtractionScorer.Score(expected, result.Receipt.Lines);
    scores.Add(ExtractionScorer.ToFixtureScore(name, expected.Count, result.Receipt.Lines.Count, detail));
    if (verbose)
    {
        Console.WriteLine($"\n[{name}] matched pairs (expected ↔ found):");
        foreach (var p in detail.Pairs) Console.WriteLine($"  {p}");
        foreach (var e in detail.MissingExpected) Console.WriteLine($"  expected · NOT FOUND → {e}");
        foreach (var f in detail.Unexpected) Console.WriteLine($"  found · NOT EXPECTED → {f}");
    }
}

var aggregate = ExtractionScorer.Aggregate(scores);

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

// Scoring math (containment matching, plural folding, aggregates) lives in Core's ExtractionScorer,
// shared with the in-app "your receipts" accuracy check so offline and in-app grading can't drift.

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

