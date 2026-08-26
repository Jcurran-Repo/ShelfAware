using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Receipts ledger: honest totals from the extracted lines (unpriced lines are counted, not
/// zeroed), the verified-for-eval trust boundary (only a human's "I checked every line" — and only
/// when an audit image exists to re-read), and removal as the confirm's inverse with its
/// consequences said BEFORE the destructive tap.
/// </summary>
public class ReceiptsPageTests : PageTestContext
{

    private readonly string dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-ui-tests", Guid.NewGuid().ToString("N"));

    internal ReceiptStorage Storage = null!;

    protected override void RegisterAdditionalServices()
    {
        var household = new FakeCurrentHousehold("hh-test"); // must match TestDb's stamping
        Storage = new ReceiptStorage(
            new AppPaths(dataDir, Path.Combine(dataDir, "receipts")),
            household,
            NullLogger<ReceiptStorage>.Instance);
        Services.AddSingleton(Storage);
        Services.AddSingleton(new ReceiptRemovalService(Factory, Storage, NullLogger<ReceiptRemovalService>.Instance));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
    }

    private int SeedReceipt(Action<Receipt>? configure = null)
    {
        using var db = Db.CreateDbContext();
        var receipt = new Receipt
        {
            Merchant = "Walmart",
            PurchasedAt = Today.AddDays(-2),
            Status = ReceiptStatus.Confirmed,
            ImagePath = "no-copy",
            Lines =
            [
                new ReceiptLine { RawText = "GV MILK", NormalizedName = "Whole Milk", Quantity = 2m, UnitPrice = 3.50m },
                new ReceiptLine { RawText = "MYSTERY", NormalizedName = "Mystery Item", Quantity = 1m },
            ],
        };
        configure?.Invoke(receipt);
        db.Receipts.Add(receipt);
        db.SaveChanges();
        return receipt.Id;
    }

    private IRenderedComponent<Receipts> RenderReceipts()
    {
        var cut = Render<Receipts>();
        cut.WaitForState(() => cut.FindAll(".receipt-card, .panel").Count > 0);
        return cut;
    }

    [Fact]
    public void Totals_come_from_the_lines_and_unpriced_lines_are_disclosed_not_zeroed()
    {
        SeedReceipt();
        var cut = RenderReceipts();

        var card = cut.Find(".receipt-card");
        Assert.Contains("Walmart · 2 items", card.TextContent);
        Assert.Contains((2m * 3.50m).ToString("C"), card.QuerySelector(".receipt-total")!.TextContent);
        // The unpriced line can't be priced in, so the total SAYS it's partial — a silent $7.00
        // would read as the whole receipt.
        Assert.Contains("(1 line without a price)", card.QuerySelector("tfoot")!.TextContent);
    }

    [Fact]
    public void Prints_the_captured_money_totals_and_headlines_the_amount_paid()
    {
        // Self-consistent printed figures: subtotal + tax = total; the line items sum to only $7.00.
        SeedReceipt(r =>
        {
            r.Subtotal = 100.00m;
            r.Savings = 5.00m;
            r.Tax = 8.25m;
            r.Total = 108.25m;
        });
        var cut = RenderReceipts();

        var card = cut.Find(".receipt-card");
        var totals = card.QuerySelector(".receipt-totals")!.TextContent;
        Assert.Contains(100.00m.ToString("C"), totals);           // subtotal
        Assert.Contains("-" + 5.00m.ToString("C"), totals);       // savings shown as a reduction
        Assert.Contains(8.25m.ToString("C"), totals);             // tax
        Assert.Contains(108.25m.ToString("C"), totals);           // total

        // The headline is what was actually PAID, not the line-item sum ($7.00).
        var headline = card.QuerySelector(".receipt-total")!.TextContent;
        Assert.Contains(108.25m.ToString("C"), headline);
        Assert.DoesNotContain((2m * 3.50m).ToString("C"), headline);

        // …and the savings win gets a chip.
        Assert.Contains("saved " + 5.00m.ToString("C"), card.TextContent);
    }

    [Fact]
    public void A_receipt_without_captured_totals_falls_back_to_the_line_item_sum()
    {
        SeedReceipt(); // no printed totals
        var cut = RenderReceipts();

        var card = cut.Find(".receipt-card");
        Assert.Null(card.QuerySelector(".receipt-totals"));                 // no printed breakdown
        Assert.Contains((2m * 3.50m).ToString("C"), card.QuerySelector(".receipt-total")!.TextContent);
        Assert.Null(card.QuerySelector(".chip-saved"));                    // no savings chip
        Assert.Empty(cut.FindAll(".receipt-running"));                     // no running-saved line
    }

    [Fact]
    public void The_running_saved_total_sums_confirmed_receipts_only()
    {
        SeedReceipt(r => { r.Savings = 5.00m; r.Tax = 8.25m; });
        SeedReceipt(r => { r.Savings = 3.00m; r.Tax = 4.00m; });
        // A pending scan carries savings too, but an unreviewed figure must not inflate the tally.
        SeedReceipt(r => { r.Status = ReceiptStatus.PendingReview; r.Savings = 99.00m; r.Tax = 50.00m; });
        var cut = RenderReceipts();

        var running = cut.Find(".receipt-running").TextContent;
        Assert.Contains(8.00m.ToString("C"), running);       // 5 + 3, NOT 107
        Assert.DoesNotContain(99.00m.ToString("C"), running);
        Assert.Contains(12.25m.ToString("C"), running);      // tax 8.25 + 4.00, NOT 62.25
    }

    [Fact]
    public void The_running_line_reports_tax_alone_without_a_zero_savings_clause()
    {
        // A household with tax but no captured savings must never read "you've saved $0.00 …".
        SeedReceipt(r => { r.Tax = 6.00m; r.Savings = null; });
        var cut = RenderReceipts();

        var running = cut.Find(".receipt-running").TextContent;
        Assert.Contains(6.00m.ToString("C"), running);        // "…paid $6.00 in tax."
        Assert.Contains("paid", running);
        Assert.DoesNotContain("saved", running);
        Assert.DoesNotContain(0m.ToString("C"), running);     // no "$0.00"
    }

    [Fact]
    public void The_running_line_reports_savings_alone_without_a_tax_clause()
    {
        SeedReceipt(r => { r.Savings = 4.00m; r.Tax = null; });
        var cut = RenderReceipts();

        var running = cut.Find(".receipt-running").TextContent;
        Assert.Contains("saved", running);
        Assert.Contains(4.00m.ToString("C"), running);        // "…you've saved $4.00."
        Assert.DoesNotContain("tax", running);
    }

    [Fact]
    public void Receipts_order_newest_first_and_discarded_ones_do_not_exist_here()
    {
        SeedReceipt(r => { r.Merchant = "Older"; r.PurchasedAt = Today.AddDays(-9); });
        SeedReceipt(r => { r.Merchant = "Newer"; r.PurchasedAt = Today.AddDays(-1); });
        SeedReceipt(r => { r.Merchant = "Gone"; r.Status = ReceiptStatus.Discarded; });
        var cut = RenderReceipts();

        var merchants = cut.FindAll(".receipt-card summary").Select(s => s.TextContent).ToList();
        Assert.Equal(2, merchants.Count);
        Assert.Contains("Newer", merchants[0]);
        Assert.Contains("Older", merchants[1]);
        Assert.DoesNotContain("Gone", cut.Markup);
    }

    [Fact]
    public void A_pending_receipt_is_flagged_and_points_back_at_the_review_queue()
    {
        SeedReceipt(r => r.Status = ReceiptStatus.PendingReview);
        var cut = RenderReceipts();

        var card = cut.Find(".receipt-card");
        Assert.Contains("Pending review", card.QuerySelector(".chip")!.TextContent);
        Assert.Contains("confirm it on the Upload page before its purchases count",
            Collapsed(card));
    }

    [Fact]
    public async Task Verification_is_offered_only_with_an_audit_image_and_toggles_through_the_db()
    {
        // The audit copy is what the self-eval re-reads — a receipt without one can never be
        // graded, so offering the checkbox would be a promise the app can't keep.
        var withImageId = SeedReceipt(r => r.Merchant = "HasImage");
        var imagePath = await Storage.NewFolderAsync();
        await Storage.WritePageAsync(imagePath, 1, [1, 2, 3], "image/png");
        using (var db = Db.CreateDbContext())
        {
            db.Receipts.Single(r => r.Id == withImageId).ImagePath = imagePath;
            db.SaveChanges();
        }
        SeedReceipt(r => r.Merchant = "NoImage");
        var cut = RenderReceipts();

        var noImage = cut.FindAll(".receipt-card").Single(c => c.TextContent.Contains("NoImage"));
        Assert.Contains("No saved image for this receipt", noImage.TextContent);

        var hasImage = cut.FindAll(".receipt-card").Single(c => c.TextContent.Contains("HasImage"));
        hasImage.QuerySelectorAll("button").Single(b => b.TextContent.Contains("I checked every line")).Click();

        await cut.WaitForAssertionAsync(async () =>
        {
            await using var raw = Db.CreateUnscopedContext();
            Assert.True((await raw.Receipts.IgnoreQueryFilters().SingleAsync(r => r.Id == withImageId)).VerifiedForEval);
        });
        cut.WaitForAssertion(() =>
        {
            var card = cut.FindAll(".receipt-card").Single(c => c.TextContent.Contains("HasImage"));
            Assert.Contains("✓ Accuracy check", card.TextContent);
            Assert.Contains("Stop using for accuracy checks", card.TextContent);
        });

        // The way back down — the human can withdraw the assertion.
        cut.FindAll(".receipt-card button").Single(b => b.TextContent.Contains("Stop using")).Click();
        await cut.WaitForAssertionAsync(async () =>
        {
            await using var raw = Db.CreateUnscopedContext();
            Assert.False((await raw.Receipts.IgnoreQueryFilters().SingleAsync(r => r.Id == withImageId)).VerifiedForEval);
        });
    }

    [Fact]
    public async Task Removal_says_its_consequences_first_and_the_confirm_actually_removes()
    {
        int receiptId;
        using (var db = Db.CreateDbContext())
        {
            var receipt = new Receipt
            {
                Merchant = "Walmart",
                PurchasedAt = Today.AddDays(-2),
                Status = ReceiptStatus.Confirmed,
                ImagePath = "no-copy",
                Lines = [new ReceiptLine { RawText = "GV MILK", NormalizedName = "Whole Milk", Quantity = 1m, UnitPrice = 3.50m }],
            };
            db.Receipts.Add(receipt);
            var product = new Product { Name = "Whole Milk", Category = Category.Dairy };
            db.Products.Add(product);
            db.SaveChanges();
            // Provenance is what makes removal safe: the purchase names the confirm that made it.
            db.PurchaseEvents.Add(new PurchaseEvent
            {
                ProductId = product.Id, PurchasedAt = Today.AddDays(-2), Quantity = 1m, ReceiptId = receipt.Id,
            });
            db.SaveChanges();
            receiptId = receipt.Id;
        }
        var cut = RenderReceipts();

        // Consequences-first: the warning and the real button appear only after the opener…
        Assert.DoesNotContain("This can't be undone", cut.Markup);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Remove receipt")).Click();
        cut.WaitForAssertion(() => Assert.Contains("and everything it recorded", cut.Markup));

        // …and Cancel walks away whole.
        cut.FindAll(".receipt-remove-actions button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("This can't be undone", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Remove receipt")).Click();
        cut.WaitForState(() => cut.Markup.Contains("Remove it"));
        cut.FindAll(".receipt-remove-actions button").Single(b => b.TextContent.Trim() == "Remove it").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Removed the receipt and its 1 purchase.", cut.Find("[role=status]").TextContent));

        await using var raw = Db.CreateUnscopedContext();
        Assert.Empty(await raw.Receipts.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await raw.PurchaseEvents.IgnoreQueryFilters().ToListAsync());
        // The product had no receipt-provenance of its own — it stays (other history may exist).
        Assert.Single(await raw.Products.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public void A_receipt_without_provenance_refuses_removal_and_says_why()
    {
        // Pre-provenance history is not undoable by design: value-matching purchases to a receipt
        // would be guessing, and removal must never guess.
        SeedReceipt();
        var cut = RenderReceipts();

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Remove receipt"));
        Assert.Contains("it can't be removed as a unit",
            Collapsed(cut.Find(".receipt-remove-actions")));
    }

    [Fact]
    public async Task The_download_link_appears_for_any_receipt_with_a_saved_copy_pending_or_confirmed()
    {
        var confirmedId = SeedReceipt(r => r.Merchant = "ConfirmedWithImage");
        await GiveImage(confirmedId);
        var pendingId = SeedReceipt(r => { r.Merchant = "PendingWithImage"; r.Status = ReceiptStatus.PendingReview; });
        await GiveImage(pendingId);
        SeedReceipt(r => r.Merchant = "NoImage"); // default ImagePath "no-copy" resolves to no pages

        var cut = RenderReceipts();

        // A saved copy → a real download link at the household-scoped endpoint for THIS receipt, marked
        // `download` so the browser saves it rather than the router treating it as a navigation.
        var confirmed = cut.FindAll(".receipt-card").Single(c => c.TextContent.Contains("ConfirmedWithImage"));
        var link = confirmed.QuerySelector(".receipt-download-actions a")!;
        Assert.Equal($"/api/receipt-image/{confirmedId}", link.GetAttribute("href"));
        Assert.True(link.HasAttribute("download"));
        Assert.Contains("Download saved copy", link.TextContent);

        // Deliberately NOT gated on Confirmed — a pending receipt's copy is downloadable too.
        var pending = cut.FindAll(".receipt-card").Single(c => c.TextContent.Contains("PendingWithImage"));
        Assert.Equal($"/api/receipt-image/{pendingId}",
            pending.QuerySelector(".receipt-download-actions a")!.GetAttribute("href"));

        // No saved copy → no link at all (nothing to download).
        var noImage = cut.FindAll(".receipt-card").Single(c => c.TextContent.Contains("NoImage"));
        Assert.Null(noImage.QuerySelector(".receipt-download-actions"));
    }

    private async Task GiveImage(int receiptId)
    {
        var imagePath = await Storage.NewFolderAsync();
        await Storage.WritePageAsync(imagePath, 0, [1, 2, 3], "image/jpeg");
        using var db = Db.CreateDbContext();
        db.Receipts.Single(r => r.Id == receiptId).ImagePath = imagePath;
        db.SaveChanges();
    }

    // ---- the pack-misread safety net ----

    [Fact]
    public void A_pack_misread_line_shows_a_soft_pack_chip_with_the_explanation()
    {
        // The persisted-flag safety net (why this whole feature exists): a line auto-confirmed with a
        // misread quantity carries a soft "pack?" chip here, so a "one 12-pack read as 12" is catchable
        // after the fact even if the done-panel was blown past.
        SeedReceipt(r => r.Lines =
        [
            new ReceiptLine
            {
                RawText = "CHARMIN 12 ROLL", NormalizedName = "Toilet Paper", Quantity = 12m,
                QuantityFlag = QuantityFlag.MissingUsualSize,
            },
        ]);

        var cut = RenderReceipts();

        var chip = cut.FindAll(".chip-duesoon").Single(c => c.TextContent.Trim() == "pack?");
        Assert.Contains("12-pack", chip.GetAttribute("title")!); // the one-definition Describe copy
    }

    [Fact]
    public void An_ordinary_line_shows_no_pack_chip()
    {
        SeedReceipt(); // default lines, QuantityFlag None
        var cut = RenderReceipts();
        Assert.DoesNotContain("pack?", cut.Markup);
    }
}
