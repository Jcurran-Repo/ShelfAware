using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;
using ShelfAware.Web.Ingest;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The graduated-trust routing that used to live in the folder importer, now applied to uploaded
/// receipts. Same contract the importer's tests pinned: Smart confirms only an all-trusted receipt,
/// machine confirms never write aliases or eval ground truth — plus the one deliberate tightening,
/// Smart refuses to guess a missing purchase date.
/// </summary>
public class ReceiptAutoConfirmerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();

    public void Dispose() => _db.Dispose();

    private ReceiptAutoConfirmer Confirmer() => new(
        _db, _settings, new ReceiptConfirmationService(_db), new ReceiptDuplicateDetector(_db),
        NullLogger<ReceiptAutoConfirmer>.Instance);

    private async Task<int> SeedProduct(string name)
    {
        await using var db = _db.CreateDbContext();
        var product = new Product { Name = name };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task SeedAlias(string rawText, int productId)
    {
        await using var db = _db.CreateDbContext();
        db.ProductAliases.Add(new ProductAlias { Merchant = "Walmart", RawText = rawText, ProductId = productId });
        await db.SaveChangesAsync();
    }

    private record SeedLine(string Raw, string Name, decimal Confidence, string? Suggested = null, string[]? Tags = null);

    /// <summary>A receipt exactly as either upload path persists it: PendingReview, lines carrying
    /// tags + the model's suggestion.</summary>
    private async Task<int> SeedReceipt(DateOnly? purchasedAt, params SeedLine[] lines)
    {
        await using var db = _db.CreateDbContext();
        var receipt = new Receipt
        {
            Merchant = "Walmart",
            PurchasedAt = purchasedAt,
            ImagePath = "test-receipt",
            Lines = lines.Select(l => new ReceiptLine
            {
                RawText = l.Raw,
                NormalizedName = l.Name,
                Quantity = 1,
                Confidence = l.Confidence,
                SuggestedProduct = l.Suggested,
                TagsJson = ReceiptConfirmationService.SerializeTags(l.Tags ?? []),
            }).ToList(),
        };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    private static readonly DateOnly Dated = new(2026, 7, 1);

    // --- Smart mode (the default): graduated trust -----------------------------

    [Fact]
    public async Task Smart_confirms_when_every_line_matches_a_known_product_confidently()
    {
        await SeedProduct("Whole Milk");
        var id = await SeedReceipt(Dated, new SeedLine("GV WHL MLK", "Whole Milk", 0.95m, Suggested: "Whole Milk"));

        var outcome = await Confirmer().TryConfirmAsync(id);

        Assert.True(outcome.Confirmed);
        Assert.Equal(1, outcome.Purchases);
        await using var db = _db.CreateDbContext();
        var receipt = await db.Receipts.SingleAsync();
        Assert.Equal(ReceiptStatus.Confirmed, receipt.Status);
        Assert.Equal(Dated, (await db.PurchaseEvents.SingleAsync()).PurchasedAt);
        Assert.Equal(0, await db.ProductAliases.CountAsync()); // machine confirm writes NO aliases
        Assert.False(receipt.VerifiedForEval);                 // and is never accuracy ground truth
    }

    [Fact]
    public async Task Smart_queues_a_receipt_containing_an_unknown_product()
    {
        await SeedProduct("Whole Milk");
        var id = await SeedReceipt(Dated,
            new SeedLine("GV WHL MLK", "Whole Milk", 0.95m, Suggested: "Whole Milk"),
            new SeedLine("DRAGON SALSA", "Dragonfruit Salsa", 0.95m, Tags: ["Condiment"]));

        var outcome = await Confirmer().TryConfirmAsync(id);

        Assert.False(outcome.Confirmed);
        await using var db = _db.CreateDbContext();
        var receipt = await db.Receipts.Include(r => r.Lines).SingleAsync();
        Assert.Equal(ReceiptStatus.PendingReview, receipt.Status);
        Assert.Equal(0, await db.PurchaseEvents.CountAsync());
        // The queued lines keep everything review needs — tags and the model's suggestion included.
        var salsa = receipt.Lines.Single(l => l.NormalizedName == "Dragonfruit Salsa");
        Assert.Equal(["Condiment"], ReceiptConfirmationService.DeserializeTags(salsa.TagsJson));
    }

    [Fact]
    public async Task Smart_queues_a_low_confidence_line_even_when_it_matches()
    {
        await SeedProduct("Whole Milk");
        var id = await SeedReceipt(Dated, new SeedLine("GV WHL M?K", "Whole Milk", 0.5m, Suggested: "Whole Milk"));

        Assert.False((await Confirmer().TryConfirmAsync(id)).Confirmed);
    }

    [Fact]
    public async Task Smart_trusts_a_learned_alias_whatever_the_confidence()
    {
        var productId = await SeedProduct("Whole Milk");
        await SeedAlias("GV WHL M?K", productId);
        var id = await SeedReceipt(Dated, new SeedLine("GV WHL M?K", "Whole Milk", 0.4m));

        var outcome = await Confirmer().TryConfirmAsync(id);

        Assert.True(outcome.Confirmed); // a human taught this pairing — that vouches for the line
        await using var db = _db.CreateDbContext();
        Assert.Equal(productId, (await db.PurchaseEvents.SingleAsync()).ProductId);
    }

    [Fact]
    public async Task Smart_queues_when_no_purchase_date_was_detected()
    {
        // Stricter than the retired folder importer: the date drives every prediction, and "assume
        // today" is exactly the silent guess review exists to catch.
        await SeedProduct("Whole Milk");
        var id = await SeedReceipt(purchasedAt: null,
            new SeedLine("GV WHL MLK", "Whole Milk", 0.95m, Suggested: "Whole Milk"));

        Assert.False((await Confirmer().TryConfirmAsync(id)).Confirmed);
        await using var db = _db.CreateDbContext();
        Assert.Equal(ReceiptStatus.PendingReview, (await db.Receipts.SingleAsync()).Status);
    }

    // --- explicit modes ----------------------------------------------------------

    [Fact]
    public async Task Review_mode_queues_even_confident_matches()
    {
        await _settings.SetAsync(SettingKeys.ImportMode, "Review");
        await SeedProduct("Whole Milk");
        var id = await SeedReceipt(Dated, new SeedLine("GV WHL MLK", "Whole Milk", 0.95m, Suggested: "Whole Milk"));

        Assert.False((await Confirmer().TryConfirmAsync(id)).Confirmed);
    }

    [Fact]
    public async Task Auto_mode_confirms_everything_including_new_products_and_undated_receipts()
    {
        await _settings.SetAsync(SettingKeys.ImportMode, "Auto");
        var id = await SeedReceipt(purchasedAt: null, new SeedLine("DRAGON SALSA", "Dragonfruit Salsa", 0.4m));

        var outcome = await Confirmer().TryConfirmAsync(id);

        Assert.True(outcome.Confirmed);
        Assert.Equal(1, outcome.NewProducts);
        await using var db = _db.CreateDbContext();
        Assert.NotNull(await db.Products.SingleOrDefaultAsync(p => p.Name == "Dragonfruit Salsa"));
        // Auto keeps its all-or-nothing contract: an undated receipt records as today.
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), (await db.PurchaseEvents.SingleAsync()).PurchasedAt);
    }

    [Fact]
    public async Task Legacy_autoconfirm_false_still_means_review_everything()
    {
        await _settings.SetAsync(SettingKeys.AutoConfirmImports, "false"); // pre-ImportMode setting
        await SeedProduct("Whole Milk");
        var id = await SeedReceipt(Dated, new SeedLine("GV WHL MLK", "Whole Milk", 0.95m, Suggested: "Whole Milk"));

        Assert.False((await Confirmer().TryConfirmAsync(id)).Confirmed);
    }

    // --- guard rails ----------------------------------------------------------------

    [Fact]
    public async Task An_exact_duplicate_queues_even_in_Auto_mode()
    {
        // Auto means "confirm everything" — everything except a silent double-recording, which is
        // the one mistake this router must never automate.
        await _settings.SetAsync(SettingKeys.ImportMode, "Auto");
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt
            {
                Merchant = "Walmart", PurchasedAt = Dated, ImagePath = "original",
                Status = ReceiptStatus.Confirmed,
                Lines = [new ReceiptLine { RawText = "GV WHL MLK", NormalizedName = "Whole Milk", Quantity = 1 }],
            });
            await db.SaveChangesAsync();
        }
        var upload = await SeedReceipt(Dated, new SeedLine("GV WHL MLK", "Whole Milk", 0.95m));

        var outcome = await Confirmer().TryConfirmAsync(upload);

        Assert.False(outcome.Confirmed);
        Assert.NotNull(outcome.Duplicate);
        await using var check = _db.CreateDbContext();
        Assert.Equal(ReceiptStatus.PendingReview,
            (await check.Receipts.SingleAsync(r => r.Id == upload)).Status);
    }

    [Fact]
    public async Task A_zero_line_receipt_always_queues_even_in_Auto_mode()
    {
        // Confirming an empty receipt (a failed read, or not a receipt at all) would just hide it.
        await _settings.SetAsync(SettingKeys.ImportMode, "Auto");
        var id = await SeedReceipt(Dated);

        Assert.False((await Confirmer().TryConfirmAsync(id)).Confirmed);
        await using var db = _db.CreateDbContext();
        Assert.Equal(ReceiptStatus.PendingReview, (await db.Receipts.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_receipt_that_is_not_pending_is_left_alone()
    {
        await _settings.SetAsync(SettingKeys.ImportMode, "Auto");
        var id = await SeedReceipt(Dated, new SeedLine("RIBS", "Pork Ribs", 0.9m));
        await using (var db = _db.CreateDbContext())
        {
            (await db.Receipts.SingleAsync()).Status = ReceiptStatus.Discarded;
            await db.SaveChangesAsync();
        }

        Assert.False((await Confirmer().TryConfirmAsync(id)).Confirmed);
        await using var check = _db.CreateDbContext();
        Assert.Equal(0, await check.PurchaseEvents.CountAsync());
        Assert.Equal(ReceiptStatus.Discarded, (await check.Receipts.SingleAsync()).Status);
    }
}
