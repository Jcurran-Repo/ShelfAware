using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;
using ShelfAware.Web.Ingest;

namespace ShelfAware.Web.Tests;

/// <summary>
/// "Is this pending receipt a re-upload of one already confirmed?" — strict by design (same date,
/// merchant, line count, lines, prices), cascading cheapest-check-first. A duplicate verdict only
/// ever QUEUES a receipt, so a false negative costs a review click, never silent double-recording.
/// </summary>
public class ReceiptDuplicateDetectorTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ReceiptDuplicateDetector Detector() => new(_db);

    private static readonly DateOnly Dated = new(2026, 7, 1);

    private record SeedLine(string Raw, string Name, decimal Price, decimal Quantity = 1);

    private async Task<int> SeedReceipt(
        ReceiptStatus status, DateOnly? purchasedAt = null, string merchant = "Walmart",
        params SeedLine[] lines)
    {
        await using var db = _db.CreateDbContext();
        var receipt = new Receipt
        {
            Merchant = merchant,
            PurchasedAt = purchasedAt ?? Dated,
            ImagePath = "dupe-test",
            Status = status,
            Lines = lines.Select(l => new ReceiptLine
            {
                RawText = l.Raw, NormalizedName = l.Name, Quantity = l.Quantity,
                UnitPrice = l.Price, Confidence = 0.9m,
            }).ToList(),
        };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    private static readonly SeedLine[] MilkAndEggs =
        [new("GV WHL MLK", "Whole Milk", 3.49m), new("LRG EGGS", "Large Eggs", 4.29m)];

    [Fact]
    public async Task An_exact_duplicate_of_a_confirmed_receipt_is_detected()
    {
        var original = await SeedReceipt(ReceiptStatus.Confirmed, lines: MilkAndEggs);
        var upload = await SeedReceipt(ReceiptStatus.PendingReview, lines: MilkAndEggs);

        var match = await Detector().FindDuplicateAsync(upload);

        Assert.NotNull(match);
        Assert.Equal(original, match.ReceiptId);
    }

    [Fact]
    public async Task Review_edits_to_the_original_names_do_not_hide_a_rescan_of_the_same_image()
    {
        // The original was reviewed and its item names corrected — but RawText is the receipt's own
        // text, review never touches it, and a re-upload of the same photo reads the same raw lines.
        await SeedReceipt(ReceiptStatus.Confirmed, lines:
            [new("GV WHL MLK", "Whole Milk (renamed by review)", 3.49m), new("LRG EGGS", "Eggs, Large", 4.29m)]);
        var upload = await SeedReceipt(ReceiptStatus.PendingReview, lines: MilkAndEggs);

        Assert.NotNull(await Detector().FindDuplicateAsync(upload));
    }

    [Fact]
    public async Task A_different_date_is_not_a_duplicate()
    {
        await SeedReceipt(ReceiptStatus.Confirmed, purchasedAt: Dated.AddDays(-7), lines: MilkAndEggs);
        var upload = await SeedReceipt(ReceiptStatus.PendingReview, lines: MilkAndEggs);

        Assert.Null(await Detector().FindDuplicateAsync(upload));
    }

    [Fact]
    public async Task A_different_line_count_is_not_a_duplicate()
    {
        await SeedReceipt(ReceiptStatus.Confirmed, lines:
            [.. MilkAndEggs, new("BREAD", "Wheat Bread", 2.79m)]);
        var upload = await SeedReceipt(ReceiptStatus.PendingReview, lines: MilkAndEggs);

        Assert.Null(await Detector().FindDuplicateAsync(upload));
    }

    [Fact]
    public async Task A_different_price_on_one_line_is_not_a_duplicate()
    {
        // Same items a week apart at new prices is a normal shop, not a re-upload.
        await SeedReceipt(ReceiptStatus.Confirmed, lines:
            [new("GV WHL MLK", "Whole Milk", 3.79m), new("LRG EGGS", "Large Eggs", 4.29m)]);
        var upload = await SeedReceipt(ReceiptStatus.PendingReview, lines: MilkAndEggs);

        Assert.Null(await Detector().FindDuplicateAsync(upload));
    }

    [Fact]
    public async Task Only_confirmed_receipts_count_as_originals()
    {
        // A queued twin is one recording waiting to happen, not a recording to protect against.
        await SeedReceipt(ReceiptStatus.PendingReview, lines: MilkAndEggs);
        await SeedReceipt(ReceiptStatus.Discarded, lines: MilkAndEggs);
        var upload = await SeedReceipt(ReceiptStatus.PendingReview, lines: MilkAndEggs);

        Assert.Null(await Detector().FindDuplicateAsync(upload));
    }

    [Fact]
    public async Task An_undated_upload_is_never_called_a_duplicate()
    {
        await SeedReceipt(ReceiptStatus.Confirmed, lines: MilkAndEggs);
        int undated;
        await using (var db = _db.CreateDbContext())
        {
            var receipt = new Receipt
            {
                Merchant = "Walmart", PurchasedAt = null, ImagePath = "dupe-test",
                Lines = [new ReceiptLine { RawText = "GV WHL MLK", NormalizedName = "Whole Milk", Quantity = 1, UnitPrice = 3.49m }],
            };
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync();
            undated = receipt.Id;
        }

        Assert.Null(await Detector().FindDuplicateAsync(undated));
    }

    [Fact]
    public async Task Another_households_identical_receipt_is_invisible()
    {
        await SeedReceipt(ReceiptStatus.Confirmed, lines: MilkAndEggs);

        _db.HouseholdId = "hh-other";
        var upload = await SeedReceipt(ReceiptStatus.PendingReview, lines: MilkAndEggs);
        var match = await Detector().FindDuplicateAsync(upload);
        _db.HouseholdId = "hh-test";

        Assert.Null(match); // two households legitimately buying the same things is not a duplicate
    }
}
