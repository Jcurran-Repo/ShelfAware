using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Evaluation;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Settings;
using ShelfAware.Llm;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Accuracy page: fixture eval results rendered against their targets, the walk-forward
/// backtest computed live from real history, and the self-check's cost discipline — its last run
/// renders from storage and grading NEVER happens on page load (a vision call per receipt is
/// spent by the button alone; the extractor here throws to prove no load path reaches it).
/// </summary>
public class AccuracyPageTests : PageTestContext
{

    private readonly string webRoot =
        Path.Combine(Path.GetTempPath(), "shelfaware-ui-tests", Guid.NewGuid().ToString("N"));

    private sealed class ThrowingExtractor : IReceiptExtractor
    {
        public Task<ExtractionResult> ExtractAsync(
            IReadOnlyList<ReceiptAttachment> pages, IReadOnlyList<string>? knownProductNames = null,
            IReadOnlyList<string>? knownTags = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The page must never extract on load — grading is button-only.");
    }

    private sealed class FakeWebHostEnvironment(string webRootPath) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "ShelfAware.Web.UI.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = webRootPath;
        public string EnvironmentName { get; set; } = "Development";
    }

    protected override void RegisterAdditionalServices()
    {
        Directory.CreateDirectory(webRoot);
        var household = new FakeCurrentHousehold("hh-test");
        var storage = new ReceiptStorage(
            new AppPaths(webRoot, Path.Combine(webRoot, "receipts")), household, NullLogger<ReceiptStorage>.Instance);
        var aiSettings = new CircuitAiSettings(Options.Create(new LlmOptions()));
        Services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment(webRoot));
        Services.AddSingleton(new ReceiptSelfEval(
            Factory, new ThrowingExtractor(), aiSettings, AppSettings, storage, NullLogger<ReceiptSelfEval>.Instance));
        Services.AddSingleton(new AiUsageMeter(
            Factory, Options.Create(new LlmOptions()), Options.Create(new ElevenLabsOptions()),
            NullLogger<AiUsageMeter>.Instance));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(webRoot)) Directory.Delete(webRoot, recursive: true);
    }

    private IRenderedComponent<Accuracy> RenderAccuracy()
    {
        var cut = Render<Accuracy>();
        cut.WaitForState(() => cut.FindAll("h2").Count > 0);
        return cut;
    }

    [Fact]
    public void With_nothing_measured_both_halves_say_how_to_get_numbers()
    {
        var cut = RenderAccuracy();

        // The extraction half points at the harness command; the prediction half names its own
        // entry bar. Empty states that teach beat empty tables.
        Assert.Contains("No eval results yet", cut.Markup);
        Assert.Contains("dotnet run --project tests/ShelfAware.Evals", cut.Find("pre").TextContent);
        Assert.Contains("Not enough history yet", cut.Markup);
        Assert.Contains("at least 3 distinct purchase",
            Collapsed(cut.Markup));
    }

    [Fact]
    public void Fixture_results_render_pass_fail_against_their_targets()
    {
        var results = new EvalResults
        {
            GeneratedAt = DateTimeOffset.Now,
            Model = "claude-haiku-4-5",
            Aggregate = new EvalAggregate { Recall = 0.99, Precision = 0.99, FieldAccuracy = 0.80 },
            Fixtures =
            [
                new FixtureScore
                {
                    Name = "walmart-2026-05-01", ExpectedLines = 20, FoundLines = 20, MatchedLines = 20,
                    Recall = 1.0, Precision = 1.0, FieldAccuracy = 0.95,
                },
                new FixtureScore { Name = "broken-fixture", Error = "image unreadable" },
            ],
        };
        File.WriteAllText(Path.Combine(webRoot, "eval-results.json"), JsonSerializer.Serialize(results));
        var cut = RenderAccuracy();

        // 0.99 clears the 0.90 targets; 0.80 misses the 0.85 field target — the stat card says
        // which side of the line each number is on, not just the number.
        var stats = cut.FindAll(".portfolio .stat").ToList();
        Assert.Contains("pass", stats.Single(s => s.TextContent.Contains("Line recall")).GetAttribute("class"));
        Assert.Contains("pass", stats.Single(s => s.TextContent.Contains("Line precision")).GetAttribute("class"));
        Assert.Contains("fail", stats.Single(s => s.TextContent.Contains("Field accuracy")).GetAttribute("class"));

        Assert.Contains("Model claude-haiku-4-5", cut.Markup);
        // An errored fixture renders its error where its scores would be — a broken fixture must
        // not vanish from a table claiming to cover the suite.
        var rows = cut.FindAll("tbody tr");
        Assert.Contains("image unreadable",
            rows.Single(r => r.TextContent.Contains("broken-fixture")).QuerySelector("td.error")!.TextContent);
    }

    [Fact]
    public void The_backtest_scores_live_history_walk_forward()
    {
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Whole Milk",
                Category = Category.Dairy,
                Purchases =
                [
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-15), Quantity = 1m },
                ],
            });
            db.SaveChanges();
        }
        var cut = RenderAccuracy();

        // A perfectly regular 15-day cadence: the third trip is predicted from the first two and
        // lands exactly — one sample, zero error, inside the ±2-day window.
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Within ±2 days", cut.Markup);
            var row = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("Whole Milk"));
            Assert.Contains("0 d", row.TextContent);
        });
        Assert.DoesNotContain("Not enough history yet", cut.Markup);
    }

    [Fact]
    public async Task The_self_check_renders_its_stored_run_and_never_grades_on_load()
    {
        // A verified receipt exists and a past run is stored. Loading the page must show BOTH
        // without touching the extractor — the harness extractor THROWS on any call, so this test
        // failing loudly is the design working.
        using (var db = Db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt
            {
                Merchant = "Walmart", PurchasedAt = Today.AddDays(-4), Status = ReceiptStatus.Confirmed,
                ImagePath = "n/a", VerifiedForEval = true,
                Lines = [new ReceiptLine { RawText = "X", NormalizedName = "Whole Milk", Quantity = 1m }],
            });
            db.SaveChanges();
        }
        var lastRun = new EvalResults
        {
            GeneratedAt = DateTimeOffset.Now.AddDays(-1),
            Model = "claude-haiku-4-5",
            Aggregate = new EvalAggregate { Recall = 1.0, Precision = 1.0, FieldAccuracy = 1.0 },
            Fixtures = [new FixtureScore { Name = "walmart 2026-07-27", ExpectedLines = 1, FoundLines = 1, MatchedLines = 1, Recall = 1, Precision = 1, FieldAccuracy = 1 }],
        };
        await AppSettings.SetAsync(SettingKeys.SelfEvalResults, JsonSerializer.Serialize(lastRun));

        var cut = RenderAccuracy();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("walmart 2026-07-27", cut.Markup);
            var button = cut.FindAll("button").Single(b => b.TextContent.Contains("Grade"));
            Assert.Contains("Grade again (1 verified receipt)", button.TextContent);
        });
        Assert.Contains("Today so far", cut.Markup); // the cost disclaimer carries today's usage
    }

    [Fact]
    public void No_verified_receipts_means_an_invitation_not_a_grade_button()
    {
        var cut = RenderAccuracy();

        Assert.Contains("No verified receipts yet", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Grade"));
    }
}
