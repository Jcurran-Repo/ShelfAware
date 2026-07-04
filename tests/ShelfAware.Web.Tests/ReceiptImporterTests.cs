using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;
using ShelfAware.Web.Ingest;

namespace ShelfAware.Web.Tests;

public class ReceiptImporterTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly FakeInbox _inbox = new();
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-web-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private ReceiptImporter Importer(FakeExtractor extractor) => new(
        _db, extractor, _inbox, _settings, new ReceiptConfirmationService(_db),
        new AppPaths(_dataDir, Path.Combine(_dataDir, "receipts")),
        NullLogger<ReceiptImporter>.Instance);

    private async Task SeedProduct(string name, params string[] tags)
    {
        await using var db = _db.CreateDbContext();
        db.Products.Add(new Product { Name = name, Tags = tags.Select(t => new ProductTag { Value = t }).ToList() });
        await db.SaveChangesAsync();
    }

    private static ExtractionResult Extracted(params ExtractedLine[] lines) =>
        ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Walmart",
            PurchaseDate = new DateOnly(2026, 7, 1),
            Lines = [.. lines],
        }, "{}");

    private static ExtractedLine Line(string raw, string name, decimal confidence,
        string? suggested = null, string[]? tags = null) => new()
    {
        RawText = raw,
        NormalizedName = name,
        Quantity = 1,
        Confidence = confidence,
        SuggestedProductName = suggested,
        Tags = tags ?? [],
    };

    // --- Smart mode (the default): graduated trust -----------------------------

    [Fact]
    public async Task Smart_confirms_when_every_line_matches_a_known_product_confidently()
    {
        await SeedProduct("Whole Milk");
        _inbox.Files["a.jpg"] = [1];
        var extractor = new FakeExtractor(Extracted(Line("GV WHL MLK", "Whole Milk", 0.95m, suggested: "Whole Milk")));

        var summary = await Importer(extractor).ImportNewAsync();

        Assert.Equal(1, summary.Imported);
        Assert.Equal(0, summary.AwaitingReview);
        await using var db = _db.CreateDbContext();
        Assert.Equal(ReceiptStatus.Confirmed, (await db.Receipts.SingleAsync()).Status);
        Assert.Equal(1, await db.PurchaseEvents.CountAsync());
        Assert.Equal(0, await db.ProductAliases.CountAsync()); // machine confirm writes NO aliases
    }

    [Fact]
    public async Task Smart_queues_a_receipt_containing_an_unknown_product()
    {
        await SeedProduct("Whole Milk");
        _inbox.Files["a.jpg"] = [1];
        var extractor = new FakeExtractor(Extracted(
            Line("GV WHL MLK", "Whole Milk", 0.95m, suggested: "Whole Milk"),
            Line("DRAGON SALSA", "Dragonfruit Salsa", 0.95m, tags: ["Condiment"])));

        var summary = await Importer(extractor).ImportNewAsync();

        Assert.Equal(0, summary.Imported);
        Assert.Equal(1, summary.AwaitingReview);
        await using var db = _db.CreateDbContext();
        var receipt = await db.Receipts.Include(r => r.Lines).SingleAsync();
        Assert.Equal(ReceiptStatus.PendingReview, receipt.Status);
        Assert.Equal(0, await db.PurchaseEvents.CountAsync());
        // The queued lines keep everything review needs — tags and the model's suggestion included.
        var salsa = receipt.Lines.Single(l => l.NormalizedName == "Dragonfruit Salsa");
        Assert.Equal(["Condiment"], ReceiptConfirmationService.DeserializeTags(salsa.TagsJson));
        var milk = receipt.Lines.Single(l => l.NormalizedName == "Whole Milk");
        Assert.Equal("Whole Milk", milk.SuggestedProduct);
    }

    [Fact]
    public async Task Smart_queues_a_low_confidence_line_even_when_it_matches()
    {
        await SeedProduct("Whole Milk");
        _inbox.Files["a.jpg"] = [1];
        var extractor = new FakeExtractor(Extracted(Line("GV WHL M?K", "Whole Milk", 0.5m, suggested: "Whole Milk")));

        var summary = await Importer(extractor).ImportNewAsync();

        Assert.Equal(1, summary.AwaitingReview);
    }

    // --- explicit modes ----------------------------------------------------------

    [Fact]
    public async Task Review_mode_queues_even_confident_matches()
    {
        await _settings.SetAsync(SettingKeys.ImportMode, "Review");
        await SeedProduct("Whole Milk");
        _inbox.Files["a.jpg"] = [1];
        var extractor = new FakeExtractor(Extracted(Line("GV WHL MLK", "Whole Milk", 0.95m, suggested: "Whole Milk")));

        var summary = await Importer(extractor).ImportNewAsync();

        Assert.Equal(1, summary.AwaitingReview);
        Assert.Equal(0, summary.Imported);
    }

    [Fact]
    public async Task Auto_mode_confirms_everything_including_new_products()
    {
        await _settings.SetAsync(SettingKeys.ImportMode, "Auto");
        _inbox.Files["a.jpg"] = [1];
        var extractor = new FakeExtractor(Extracted(Line("DRAGON SALSA", "Dragonfruit Salsa", 0.4m)));

        var summary = await Importer(extractor).ImportNewAsync();

        Assert.Equal(1, summary.Imported);
        Assert.Equal(1, summary.NewProducts);
        await using var db = _db.CreateDbContext();
        Assert.NotNull(await db.Products.SingleOrDefaultAsync(p => p.Name == "Dragonfruit Salsa"));
    }

    [Fact]
    public async Task Legacy_autoconfirm_false_still_means_review_everything()
    {
        await _settings.SetAsync(SettingKeys.AutoConfirmImports, "false"); // pre-ImportMode setting
        await SeedProduct("Whole Milk");
        _inbox.Files["a.jpg"] = [1];
        var extractor = new FakeExtractor(Extracted(Line("GV WHL MLK", "Whole Milk", 0.95m, suggested: "Whole Milk")));

        var summary = await Importer(extractor).ImportNewAsync();

        Assert.Equal(1, summary.AwaitingReview);
    }

    // --- failure + dedup ----------------------------------------------------------

    [Fact]
    public async Task Failed_extraction_is_recorded_pending_and_counted_as_failed()
    {
        _inbox.Files["bad.jpg"] = [1];
        var extractor = new FakeExtractor(ExtractionResult.Fail("model exploded"));

        var summary = await Importer(extractor).ImportNewAsync();

        Assert.Equal(1, summary.Failed);
        await using var db = _db.CreateDbContext();
        var receipt = await db.Receipts.Include(r => r.Lines).SingleAsync();
        Assert.Equal("bad.jpg", receipt.SourceFile);            // re-scans skip it…
        Assert.Equal(ReceiptStatus.PendingReview, receipt.Status); // …and Upload lists it for retry/discard
        Assert.Empty(receipt.Lines);
    }

    [Fact]
    public async Task A_second_scan_skips_already_imported_files()
    {
        await _settings.SetAsync(SettingKeys.ImportMode, "Auto");
        _inbox.Files["a.jpg"] = [1];
        var extractor = new FakeExtractor(Extracted(Line("RIBS", "Pork Ribs", 0.9m)));
        var importer = Importer(extractor);

        await importer.ImportNewAsync();
        var second = await importer.ImportNewAsync();

        Assert.Equal(0, second.Imported + second.AwaitingReview + second.Failed);
        Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public async Task Concurrent_scans_do_not_double_import_the_same_file()
    {
        await _settings.SetAsync(SettingKeys.ImportMode, "Auto");
        _inbox.Files["a.jpg"] = [1];
        var extractor = new FakeExtractor(Extracted(Line("RIBS", "Pork Ribs", 0.9m)))
        {
            Delay = TimeSpan.FromMilliseconds(150), // hold the first scan open so the second overlaps
        };
        var importer = Importer(extractor);

        var results = await Task.WhenAll(importer.ImportNewAsync(), importer.ImportNewAsync());

        Assert.Equal(1, results.Sum(r => r.Imported));
        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Receipts.CountAsync());
        Assert.Equal(1, await db.PurchaseEvents.CountAsync());
    }

    // --- extraction hints -----------------------------------------------------------

    [Fact]
    public async Task Extractor_receives_the_product_list_and_live_tag_vocabulary()
    {
        await SeedProduct("Hot Sauce", "Spicy");
        _inbox.Files["a.jpg"] = [1];
        var extractor = new FakeExtractor(Extracted(Line("RIBS", "Pork Ribs", 0.9m)));

        await Importer(extractor).ImportNewAsync();

        Assert.Contains("Hot Sauce", extractor.LastKnownProductNames!);
        Assert.Contains("Spicy", extractor.LastKnownTags!); // stored tag
        Assert.Contains("Snack", extractor.LastKnownTags!); // seed vocabulary
    }
}
