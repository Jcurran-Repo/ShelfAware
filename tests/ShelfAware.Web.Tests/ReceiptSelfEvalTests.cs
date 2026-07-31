using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Settings;
using ShelfAware.Llm;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The in-app accuracy check (92 lines, zero coverage until the 7/30 audit). The contracts that
/// matter: only confirmed+verified receipts are graded, a missing audit copy or a failed extraction
/// costs that RECEIPT and not the run, and the last run persists per household so navigating away
/// doesn't waste the spend.
/// </summary>
public sealed class ReceiptSelfEvalTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly FakeExtractor _extractor = new();
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-selfeval-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private ReceiptStorage Storage() => new(
        new AppPaths(_dataDir, Path.Combine(_dataDir, "receipts")),
        new FakeCurrentHousehold(),
        NullLogger<ReceiptStorage>.Instance);

    private ReceiptSelfEval Service() => new(
        _db, _extractor, new CircuitAiSettings(Options.Create(new LlmOptions())), _settings,
        Storage(), NullLogger<ReceiptSelfEval>.Instance);

    private sealed class FakeExtractor : IReceiptExtractor
    {
        private readonly Queue<Func<ExtractionResult>> _script = new();
        public int Calls { get; private set; }

        public void Enqueue(Func<ExtractionResult> next) => _script.Enqueue(next);

        public Task<ExtractionResult> ExtractAsync(
            IReadOnlyList<ReceiptAttachment> attachments,
            IReadOnlyList<string>? knownProductNames = null,
            IReadOnlyList<string>? knownTags = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_script.Dequeue()());
        }
    }

    private static ExtractionResult Extraction(params string[] names) =>
        new()
        {
            Success = true,
            Receipt = new ExtractedReceipt
            {
                Lines = names.Select(n => new ExtractedLine
                {
                    RawText = n, NormalizedName = n, Quantity = 1, Category = Category.Pantry, Confidence = 0.9m,
                }).ToList(),
            },
        };

    /// <summary>A confirmed receipt with one line per name; verified unless said otherwise, with a
    /// stored audit page unless said otherwise.</summary>
    private async Task<int> SeedReceipt(
        bool verified = true, bool withImage = true, DateOnly? purchasedAt = null, params string[] names)
    {
        var imagePath = "missing/nothing-here";
        if (withImage)
        {
            var storage = Storage();
            imagePath = await storage.NewFolderAsync();
            await storage.WritePageAsync(imagePath, 0, [1, 2, 3], "image/jpeg");
        }
        await using var db = _db.CreateDbContext();
        var receipt = new Receipt
        {
            Merchant = "Walmart",
            PurchasedAt = purchasedAt ?? new DateOnly(2026, 7, 1),
            ImagePath = imagePath,
            Status = ReceiptStatus.Confirmed,
            VerifiedForEval = verified,
            Lines = names.Select(n => new ReceiptLine
            {
                RawText = n, NormalizedName = n, Quantity = 1, Category = Category.Pantry,
            }).ToList(),
        };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    [Fact]
    public async Task Counts_only_confirmed_verified_receipts()
    {
        await SeedReceipt(verified: true, names: "Whole Milk");
        await SeedReceipt(verified: false, names: "Pork Ribs");

        Assert.Equal(1, await Service().CountVerifiedAsync());
    }

    [Fact]
    public async Task A_run_grades_the_stored_copy_persists_and_the_last_run_round_trips()
    {
        await SeedReceipt(names: ["Whole Milk", "Large Eggs"]);
        _extractor.Enqueue(() => Extraction("Whole Milk", "Large Eggs"));

        var results = await Service().RunAsync();

        var fixture = Assert.Single(results.Fixtures);
        Assert.Null(fixture.Error);
        Assert.Equal(1.0, results.Aggregate.Recall);
        Assert.Equal(1.0, results.Aggregate.Precision);

        // Persisted per household, and readable back the way the page reads it on load.
        var reloaded = await Service().GetLastRunAsync();
        Assert.NotNull(reloaded);
        Assert.Equal(1.0, reloaded.Aggregate.Recall);
        Assert.Single(reloaded.Fixtures);
    }

    [Fact]
    public async Task A_missing_audit_copy_errors_that_receipt_without_spending_a_vision_call()
    {
        await SeedReceipt(withImage: false, names: "Whole Milk");

        var results = await Service().RunAsync();

        Assert.Contains("saved image missing", Assert.Single(results.Fixtures).Error);
        Assert.Equal(0, _extractor.Calls); // no image, no spend
    }

    [Fact]
    public async Task A_failed_extraction_reports_its_error_on_that_fixture()
    {
        await SeedReceipt(names: "Whole Milk");
        _extractor.Enqueue(() => new ExtractionResult { Success = false, Error = "model refused" });

        var results = await Service().RunAsync();

        Assert.Equal("model refused", Assert.Single(results.Fixtures).Error);
    }

    [Fact]
    public async Task One_receipt_that_throws_costs_that_receipt_and_not_the_run()
    {
        // Newest first: the throwing receipt is graded first, and the second must still be graded —
        // one unreadable page (or a transient API blowup) must not sink the whole spend.
        await SeedReceipt(purchasedAt: new DateOnly(2026, 7, 20), names: "Pork Ribs");
        await SeedReceipt(purchasedAt: new DateOnly(2026, 7, 1), names: "Whole Milk");
        _extractor.Enqueue(() => throw new IOException("page unreadable"));
        _extractor.Enqueue(() => Extraction("Whole Milk"));

        var results = await Service().RunAsync();

        Assert.Equal(2, results.Fixtures.Count);
        Assert.Contains("grading failed", results.Fixtures[0].Error);
        Assert.Null(results.Fixtures[1].Error);
        Assert.Equal(1.0, results.Aggregate.Recall); // errored fixtures don't drag the aggregate
    }

    [Fact]
    public async Task Corrupt_stored_results_read_as_no_run_rather_than_crashing_the_page()
    {
        await _settings.SetAsync(SettingKeys.SelfEvalResults, "{ not json");

        Assert.Null(await Service().GetLastRunAsync());
    }
}
