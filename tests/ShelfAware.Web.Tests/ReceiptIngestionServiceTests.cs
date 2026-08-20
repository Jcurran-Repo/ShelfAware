using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;
using ShelfAware.Web.Ingest;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The one path from receipt images to a persisted receipt, lifted out of <c>Upload.razor</c> so the page
/// and the upload endpoint share it. These pin the behaviour that used to be private page methods (items
/// 37–48): a read persists a PendingReview receipt with its lines; several pages are ONE receipt; a FAILED
/// read still keeps the audit copy so Retry can re-run; the auto-confirmer's verdict rides out on the
/// outcome; and <see cref="ReceiptIngestionService.RetryAsync"/> re-extracts from the saved copy.
/// </summary>
public class ReceiptIngestionServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly StubExtractor _extractor = new();
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-ingest-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }

    private ReceiptStorage Storage() => new(
        new AppPaths(_dataDir, Path.Combine(_dataDir, "receipts")),
        new FakeCurrentHousehold("hh-test"), NullLogger<ReceiptStorage>.Instance);

    private ReceiptIngestionService Service()
    {
        var confirmer = new ReceiptConfirmationService(_db, UndoTesting.Log(_db));
        var duplicates = new ReceiptDuplicateDetector(_db);
        var auto = new ReceiptAutoConfirmer(_db, _settings, confirmer, duplicates,
            NullLogger<ReceiptAutoConfirmer>.Instance);
        return new ReceiptIngestionService(_db, _extractor, Storage(), auto,
            NullLogger<ReceiptIngestionService>.Instance);
    }

    private static readonly DateOnly Dated = new(2026, 7, 1);

    private static ReceiptAttachment Page() => new([1, 2, 3], "image/jpeg");

    private static ExtractionResult Ok(DateOnly? date, string merchant, params (string Raw, string Name, decimal Conf, string? Suggested)[] lines) =>
        ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = merchant,
            PurchaseDate = date,
            Lines = lines.Select(l => new ExtractedLine
            {
                RawText = l.Raw,
                NormalizedName = l.Name,
                Confidence = l.Conf,
                SuggestedProductName = l.Suggested,
            }).ToList(),
        }, rawJson: "{\"stub\":true}");

    private async Task<int> SeedProduct(string name)
    {
        await using var db = _db.CreateDbContext();
        var product = new Product { Name = name };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    // --- a fresh ingest ----------------------------------------------------------

    [Fact]
    public async Task A_successful_read_persists_a_pending_receipt_with_its_lines_and_the_audit_copy()
    {
        await _settings.SetAsync(SettingKeys.ImportMode, "Review"); // no auto-confirm — isolate the persist
        _extractor.Respond = _ => Ok(Dated, "Walmart",
            ("GV WHL MLK", "Whole Milk", 0.9m, null), ("BANANAS", "Bananas", 0.9m, null));

        var outcome = await Service().IngestAsync([Page()]);

        Assert.True(outcome.Success);
        Assert.True(outcome.ReceiptId > 0);
        Assert.Equal("Walmart", outcome.Merchant);
        Assert.Equal(Dated, outcome.PurchasedAt);
        Assert.Equal(2, outcome.LineCount);
        Assert.False(outcome.AutoConfirmed);

        await using var db = _db.CreateDbContext();
        var receipt = await db.Receipts.Include(r => r.Lines).SingleAsync();
        Assert.Equal(ReceiptStatus.PendingReview, receipt.Status);
        Assert.Equal(2, receipt.Lines.Count);
        Assert.True(Storage().HasPages(receipt.ImagePath)); // the audit copy is on disk
    }

    [Fact]
    public async Task Several_pages_are_one_receipt_read_in_a_single_extraction_call()
    {
        await _settings.SetAsync(SettingKeys.ImportMode, "Review");
        _extractor.Respond = _ => Ok(Dated, "Walmart", ("A", "Apples", 0.9m, null));

        var outcome = await Service().IngestAsync([Page(), Page(), Page()]);

        Assert.True(outcome.Success);
        Assert.Equal(1, _extractor.Calls);          // one receipt, not three
        Assert.Equal(3, _extractor.PageCounts[0]);  // all three pages went to that one call
        await using var db = _db.CreateDbContext();
        var receipt = await db.Receipts.SingleAsync();
        Assert.Equal(3, Storage().Pages(receipt.ImagePath).Count); // three page files saved
    }

    [Fact]
    public async Task A_failed_read_still_persists_the_receipt_so_retry_can_re_run()
    {
        _extractor.Respond = _ => ExtractionResult.Fail("couldn't read it", rawJson: "{\"raw\":1}");

        var outcome = await Service().IngestAsync([Page()]);

        Assert.False(outcome.Success);
        Assert.Equal("couldn't read it", outcome.Error);
        Assert.True(outcome.ReceiptId > 0);

        await using var db = _db.CreateDbContext();
        var receipt = await db.Receipts.Include(r => r.Lines).SingleAsync();
        Assert.Equal(ReceiptStatus.PendingReview, receipt.Status);
        Assert.Empty(receipt.Lines);
        Assert.Equal("{\"raw\":1}", receipt.RawModelJson);      // raw output kept for audit
        Assert.True(Storage().HasPages(receipt.ImagePath));     // the image kept for Retry
    }

    [Fact]
    public async Task An_ingest_with_no_pages_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().IngestAsync([]));
    }

    // --- the auto-confirm verdict rides out on the outcome -----------------------

    [Fact]
    public async Task Smart_auto_confirms_a_trusted_receipt_and_the_outcome_carries_the_counts()
    {
        await SeedProduct("Whole Milk"); // Smart is the default mode
        _extractor.Respond = _ => Ok(Dated, "Walmart", ("GV WHL MLK", "Whole Milk", 0.95m, "Whole Milk"));

        var outcome = await Service().IngestAsync([Page()]);

        Assert.True(outcome.Success);
        Assert.True(outcome.AutoConfirmed);
        Assert.Equal(1, outcome.Purchases);
        await using var db = _db.CreateDbContext();
        Assert.Equal(ReceiptStatus.Confirmed, (await db.Receipts.SingleAsync()).Status);
        Assert.Equal(1, await db.PurchaseEvents.CountAsync());
    }

    [Fact]
    public async Task An_untrusted_receipt_falls_through_to_review()
    {
        // Smart default, unknown product → the router won't vouch for it.
        _extractor.Respond = _ => Ok(Dated, "Walmart", ("DRAGON SALSA", "Dragonfruit Salsa", 0.95m, null));

        var outcome = await Service().IngestAsync([Page()]);

        Assert.True(outcome.Success);
        Assert.False(outcome.AutoConfirmed);
        await using var db = _db.CreateDbContext();
        Assert.Equal(ReceiptStatus.PendingReview, (await db.Receipts.SingleAsync()).Status);
        Assert.Equal(0, await db.PurchaseEvents.CountAsync());
    }

    [Fact]
    public async Task An_exact_duplicate_is_flagged_and_never_auto_confirmed()
    {
        await _settings.SetAsync(SettingKeys.ImportMode, "Auto"); // even confirm-everything queues a dupe
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt
            {
                Merchant = "Walmart", PurchasedAt = Dated, ImagePath = "original", Status = ReceiptStatus.Confirmed,
                Lines = [new ReceiptLine { RawText = "GV WHL MLK", NormalizedName = "Whole Milk", Quantity = 1 }],
            });
            await db.SaveChangesAsync();
        }
        _extractor.Respond = _ => Ok(Dated, "Walmart", ("GV WHL MLK", "Whole Milk", 0.95m, null));

        var outcome = await Service().IngestAsync([Page()]);

        Assert.True(outcome.Success);
        Assert.False(outcome.AutoConfirmed);
        Assert.True(outcome.PossibleDuplicate);
    }

    // --- retry re-extracts from the saved copy -----------------------------------

    [Fact]
    public async Task Retry_re_extracts_from_the_saved_copy_and_replaces_the_lines()
    {
        _extractor.Respond = _ => ExtractionResult.Fail("blip"); // first read fails, but keeps the audit copy
        var failed = await Service().IngestAsync([Page()]);
        Assert.False(failed.Success);

        _extractor.Respond = _ => Ok(Dated, "Walmart", ("GV WHL MLK", "Whole Milk", 0.9m, null));
        var result = await Service().RetryAsync(failed.ReceiptId);

        Assert.True(result.Ok);
        await using var db = _db.CreateDbContext();
        var receipt = await db.Receipts.Include(r => r.Lines).SingleAsync();
        Assert.Single(receipt.Lines);
        Assert.Equal(Dated, receipt.PurchasedAt);
        Assert.Equal(ReceiptStatus.PendingReview, receipt.Status); // retry goes to review, never auto-confirms
    }

    [Fact]
    public async Task Retry_reports_gone_for_an_unknown_receipt()
    {
        var result = await Service().RetryAsync(9999);
        Assert.Equal(ReceiptIngestionService.RetryFailure.Gone, result.Failure);
    }

    [Fact]
    public async Task Retry_reports_a_missing_copy_when_the_image_is_gone()
    {
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var r = new Receipt { ImagePath = "receipts/hh-test/missing", Status = ReceiptStatus.PendingReview };
            db.Receipts.Add(r);
            await db.SaveChangesAsync();
            id = r.Id;
        }

        var result = await Service().RetryAsync(id);
        Assert.Equal(ReceiptIngestionService.RetryFailure.MissingCopy, result.Failure);
    }

    [Fact]
    public async Task Retry_reports_the_error_when_the_read_fails_again()
    {
        _extractor.Respond = _ => ExtractionResult.Fail("still no good");
        var failed = await Service().IngestAsync([Page()]);

        var result = await Service().RetryAsync(failed.ReceiptId);

        Assert.Equal(ReceiptIngestionService.RetryFailure.ReadFailed, result.Failure);
        Assert.Equal("still no good", result.Error);
    }

    /// <summary>A stand-in extractor: returns whatever <see cref="Respond"/> is set to, and records how
    /// many pages each call was handed (so "several pages, one call" is provable).</summary>
    private sealed class StubExtractor : IReceiptExtractor
    {
        public Func<IReadOnlyList<ReceiptAttachment>, ExtractionResult> Respond { get; set; } =
            _ => ExtractionResult.Fail("no stub response set");
        public List<int> PageCounts { get; } = [];
        public int Calls => PageCounts.Count;

        public Task<ExtractionResult> ExtractAsync(
            IReadOnlyList<ReceiptAttachment> attachments,
            IReadOnlyList<string>? knownProductNames = null,
            IReadOnlyList<string>? knownTags = null,
            CancellationToken cancellationToken = default)
        {
            PageCounts.Add(attachments.Count);
            return Task.FromResult(Respond(attachments));
        }
    }
}
