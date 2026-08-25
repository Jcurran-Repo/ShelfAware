using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Tagging;
using ShelfAware.Llm;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Ingest;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Upload page over the REAL ingest stack — storage, confirmation service, auto-confirm
/// router, duplicate detector, removal — with only the extractor faked (a queue of canned
/// results). PDFs skip the browser-side image resize, so bUnit can drive the whole pipeline:
/// select → extract → route (Smart/Auto/Review) → review grid → the one confirm path → Done
/// panel → Undo.
/// </summary>
public class UploadPageTests : PageTestContext
{

    private readonly string dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-ui-tests", Guid.NewGuid().ToString("N"));

    internal ReceiptStorage Storage = null!;
    internal QueueExtractor Extractor = null!;
    internal FakeTagAdvisor TagAdvisor = null!;

    internal sealed class QueueExtractor : IReceiptExtractor
    {
        public Queue<ExtractionResult> Results { get; } = new();

        public Task<ExtractionResult> ExtractAsync(
            IReadOnlyList<ReceiptAttachment> pages, IReadOnlyList<string>? knownProductNames = null,
            IReadOnlyList<string>? knownTags = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Results.Count > 0
                ? Results.Dequeue()
                : new ExtractionResult { Success = false, Error = "no result queued", RawModelJson = "{}" });
    }

    internal sealed class FakeTagAdvisor : ITagAdvisor
    {
        public string? Synonym { get; set; }
        public Task<string?> FindSynonymAsync(string candidate, IReadOnlyList<string> existing, CancellationToken ct = default) =>
            Task.FromResult(Synonym);
    }

    // The Upload page injects AntiforgeryStateProvider for the upload endpoint's CSRF token; bUnit registers
    // none, so a render would fail to resolve it. The token value is irrelevant to these tests (the JS
    // uploader is mocked), so a fixed token is enough.
    private sealed class TestAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("test-token", "__RequestVerificationToken");
    }

    protected override void RegisterAdditionalServices()
    {
        var household = new FakeCurrentHousehold("hh-test");
        Storage = new ReceiptStorage(
            new AppPaths(dataDir, Path.Combine(dataDir, "receipts")), household, NullLogger<ReceiptStorage>.Instance);
        Extractor = new QueueExtractor();
        TagAdvisor = new FakeTagAdvisor();
        var confirmer = new ReceiptConfirmationService(Factory, ActivityLog);
        var duplicates = new ReceiptDuplicateDetector(Factory);
        Services.AddSingleton<IReceiptExtractor>(Extractor);
        Services.AddSingleton<ITagAdvisor>(TagAdvisor);
        // The Upload page injects AntiforgeryStateProvider (the upload endpoint's CSRF token). bUnit
        // registers none, so a render would fail to resolve it without this stand-in.
        Services.AddSingleton<AntiforgeryStateProvider>(new TestAntiforgery());
        Services.AddSingleton(Storage);
        Services.AddSingleton(confirmer);
        Services.AddSingleton(duplicates);
        var autoConfirmer = new ReceiptAutoConfirmer(Factory, AppSettings, confirmer, duplicates,
            NullLogger<ReceiptAutoConfirmer>.Instance);
        Services.AddSingleton(autoConfirmer);
        Services.AddSingleton(new ReceiptIngestionService(Factory, Extractor, Storage, autoConfirmer,
            NullLogger<ReceiptIngestionService>.Instance));
        Services.AddSingleton(new ReceiptRemovalService(Factory, Storage, NullLogger<ReceiptRemovalService>.Instance));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
    }

    private IRenderedComponent<Upload> RenderUpload()
    {
        var cut = Render<Upload>();
        cut.WaitForState(() => cut.FindAll("section.panel").Count > 0);
        return cut;
    }

    // Confidence 0.95: Smart's router trusts a line only at/above its 0.8 extraction-confidence
    // floor — a clean scan, so the tests exercise the resolve rules, not the floor.
    private static ExtractedLine Line(string raw, string name, decimal? price = 3.50m, string? suggested = null) =>
        new() { RawText = raw, NormalizedName = name, Quantity = 1m, UnitPrice = price, Category = Category.Dairy, SuggestedProductName = suggested, Confidence = 0.95m };

    private int SeedPending(string? merchant = "Walmart", DateOnly? purchasedAt = null, params ReceiptLine[] lines)
    {
        using var db = Db.CreateDbContext();
        var receipt = new Receipt
        {
            Merchant = merchant,
            PurchasedAt = purchasedAt,
            Status = ReceiptStatus.PendingReview,
            ImagePath = "no-copy",
            Lines = [.. lines],
        };
        db.Receipts.Add(receipt);
        db.SaveChanges();
        return receipt.Id;
    }

    private static ReceiptLine DbLine(string raw, string name, decimal? price = 3.50m) =>
        new() { RawText = raw, NormalizedName = name, Quantity = 1m, UnitPrice = price, Category = Category.Dairy };

    // ------------------------------------------------------------------------- idle + the queue

    [Fact]
    public async Task The_mode_hint_matches_the_household_setting()
    {
        // Smart is the default: the page must say uploads may record themselves BEFORE one does.
        var smart = RenderUpload();
        Assert.Contains("Smart confirm is on", smart.Markup);

        await AppSettings.SetAsync(SettingKeys.ImportMode, "Auto");
        var auto = RenderUpload();
        Assert.Contains("Confirm-everything is on", auto.Markup);

        await AppSettings.SetAsync(SettingKeys.ImportMode, "Review");
        var review = RenderUpload();
        Assert.DoesNotContain("confirm is on", review.Markup);
        Assert.DoesNotContain("Confirm-everything", review.Markup);
    }

    [Fact]
    public void The_queue_separates_readable_receipts_from_failed_reads()
    {
        SeedPending("Walmart", Today.AddDays(-2), DbLine("GV MILK", "Whole Milk"));
        SeedPending("Mystery", null); // zero lines — the extraction failed
        var cut = RenderUpload();

        cut.WaitForAssertion(() => Assert.Contains("Waiting for review (2)", cut.Markup));
        var rows = cut.FindAll(".pending-receipts li");
        var readable = rows.Single(r => r.TextContent.Contains("Walmart"));
        Assert.NotNull(readable.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Review"));
        var failed = rows.Single(r => r.TextContent.Contains("Mystery"));
        // A failed read is VISIBLE with a way back — it used to vanish, recoverable only from logs.
        Assert.Contains("couldn't be read — retry or discard", failed.TextContent);
        Assert.NotNull(failed.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Retry"));
    }

    [Fact]
    public async Task A_queued_twin_of_a_confirmed_receipt_wears_the_duplicate_chip()
    {
        // Same date, merchant, lines, prices as an already-confirmed receipt: the chip is why a
        // queued dupe gets discarded instead of rubber-stamped.
        using (var db = Db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt
            {
                Merchant = "Walmart", PurchasedAt = Today.AddDays(-2), Status = ReceiptStatus.Confirmed,
                ImagePath = "n/a", Lines = [DbLine("GV MILK", "Whole Milk")],
            });
            await db.SaveChangesAsync();
        }
        SeedPending("Walmart", Today.AddDays(-2), DbLine("GV MILK", "Whole Milk"));
        var cut = RenderUpload();

        cut.WaitForAssertion(() =>
            Assert.Contains("possible duplicate", cut.Find(".pending-receipts .chip").TextContent));
    }

    // -------------------------------------------------------------------------------- the review

    private IRenderedComponent<Upload> OpenReview(IRenderedComponent<Upload>? existing = null)
    {
        var cut = existing ?? RenderUpload();
        cut.WaitForState(() => cut.FindAll(".pending-receipts button").Count > 0);
        cut.FindAll(".pending-receipts button").Single(b => b.TextContent.Trim() == "Review").Click();
        cut.WaitForState(() => cut.FindAll(".review-actions").Count > 0);
        return cut;
    }

    private int SeedPendingWithTotals(Action<Receipt> configure)
    {
        using var db = Db.CreateDbContext();
        var receipt = new Receipt
        {
            Merchant = "Costco",
            PurchasedAt = Today.AddDays(-1),
            Status = ReceiptStatus.PendingReview,
            ImagePath = "no-copy",
            Lines = [DbLine("ITEM", "Item")],
        };
        configure(receipt);
        db.Receipts.Add(receipt);
        db.SaveChanges();
        return receipt.Id;
    }

    [Fact]
    public void The_review_screen_shows_the_totals_the_receipt_printed()
    {
        // The whole point of reading a receipt's totals is to CHECK them against the paper — which you
        // can't do if they only surface after confirming (over on /receipts). The review screen renders
        // the same shared "From the receipt" panel, so the subtotal/savings/tax/total are visible here.
        SeedPendingWithTotals(r => { r.Subtotal = 100.00m; r.Savings = 5.00m; r.Tax = 8.25m; r.Total = 108.25m; });

        var totals = OpenReview().Find(".receipt-totals").TextContent;

        Assert.Contains("From the receipt", totals);
        Assert.Contains(100.00m.ToString("C"), totals);   // subtotal
        Assert.Contains(5.00m.ToString("C"), totals);     // savings
        Assert.Contains(8.25m.ToString("C"), totals);     // tax
        Assert.Contains(108.25m.ToString("C"), totals);   // total (what you paid)
    }

    [Fact]
    public void The_review_screen_omits_the_totals_box_when_the_receipt_printed_none()
    {
        // A receipt whose printed totals extraction didn't capture shows no box at all — no empty shell,
        // no zeros pretending to be figures.
        SeedPendingWithTotals(_ => { });

        Assert.Empty(OpenReview().FindAll(".receipt-totals"));
    }

    [Fact]
    public void Review_prefills_the_product_match_by_trust_order()
    {
        int aliasedId, suggestedId, matchedId;
        using (var db = Db.CreateDbContext())
        {
            var aliased = new Product { Name = "Cod Skin Dog Treats", Category = Category.PetCare };
            var suggested = new Product { Name = "Whole Milk", Category = Category.Dairy };
            var matched = new Product { Name = "93% Lean Ground Beef", Category = Category.Meat };
            db.Products.AddRange(aliased, suggested, matched);
            db.SaveChanges();
            db.ProductAliases.Add(new ProductAlias { Merchant = "Walmart", RawText = "ASMPET COD", ProductId = aliased.Id });
            db.SaveChanges();
            (aliasedId, suggestedId, matchedId) = (aliased.Id, suggested.Id, matched.Id);
        }
        SeedPending("Walmart", Today.AddDays(-1),
            DbLine("ASMPET COD", "Dog Treats"),                                    // alias wins over everything
            new ReceiptLine { RawText = "GV MILK", NormalizedName = "Milk", Quantity = 1m, SuggestedProduct = "Whole Milk" },
            DbLine("GRND BEEF", "lean ground beef"),                               // matcher resolves fuzzily
            DbLine("ZZZ", "Completely Novel Thing"));                              // nothing → create new

        var cut = OpenReview();

        var selects = cut.FindAll("td select[aria-label^='Product match']");
        Assert.Equal(aliasedId.ToString(), selects[0].GetAttribute("value"));
        Assert.Equal(suggestedId.ToString(), selects[1].GetAttribute("value"));
        Assert.Equal(matchedId.ToString(), selects[2].GetAttribute("value"));
        Assert.Equal("0", selects[3].GetAttribute("value"));
        // The name column follows the SAME scope: an alias is a pairing the household taught, so its row
        // shows the product's OWN curated name ("Cod Skin Dog Treats") — NOT the model's opaque read
        // ("Dog Treats"). A suggestion/matcher/create-new row keeps the model's read so the reviewer can
        // verify a fuzzy match against the dropdown.
        var names = cut.FindAll("input[aria-label^='Item name']");
        Assert.Equal("Cod Skin Dog Treats", names[0].GetAttribute("value")); // alias → product name
        Assert.Equal("Milk", names[1].GetAttribute("value"));                 // suggestion → model read kept
        Assert.Equal("lean ground beef", names[2].GetAttribute("value"));     // matcher → model read kept
        Assert.Equal("Completely Novel Thing", names[3].GetAttribute("value")); // create-new → model read kept
        // Only the alias row wears the "learned from this merchant" link mark.
        Assert.Single(cut.FindAll("[title='Matched a previous receipt line from this merchant']"));
    }

    [Fact]
    public async Task An_aliased_line_confirms_under_the_products_curated_name()
    {
        // The household once taught "HONEST COW" → their "Cottage Cheese" product; the model can't read
        // that mapping off the print, so the next trip's alias pre-fills the curated name AND the confirm
        // records it — the receipt line and its purchase both read "Cottage Cheese", not the opaque
        // abbreviation, and no twin product is minted. RawText is the print and is never touched.
        int productId;
        using (var db = Db.CreateDbContext())
        {
            var cottageCheese = new Product { Name = "Cottage Cheese", Category = Category.Dairy };
            db.Products.Add(cottageCheese);
            db.SaveChanges();
            db.ProductAliases.Add(new ProductAlias { Merchant = "Costco", RawText = "HONEST COW", ProductId = cottageCheese.Id });
            db.SaveChanges();
            productId = cottageCheese.Id;
        }
        // The model re-guesses the opaque line as "HONEST COW" — the read the alias must override.
        SeedPending("Costco", Today.AddDays(-1), DbLine("HONEST COW", "HONEST COW"));

        var cut = OpenReview();
        Assert.Equal("Cottage Cheese", cut.Find("input[aria-label^='Item name']").GetAttribute("value"));

        // Confirm WITHOUT editing the line — the pre-filled curated name is what persists.
        cut.FindAll(".review-actions button").Single(b => b.TextContent.Trim() == "Confirm all").Click();
        cut.WaitForState(() => cut.FindAll(".review-actions button").Any(b => b.TextContent.Contains("Upload another")));

        await using var raw = Db.CreateUnscopedContext();
        var purchase = await raw.PurchaseEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(productId, purchase.ProductId);          // linked to the taught product, no twin minted
        var line = await raw.ReceiptLines.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("Cottage Cheese", line.NormalizedName);  // the curated name, not the model's read
        Assert.Equal("HONEST COW", line.RawText);             // the print is preserved
        Assert.Single(await raw.Products.IgnoreQueryFilters().ToListAsync()); // no second "Cottage Cheese"
    }

    [Fact]
    public void Review_does_not_pre_select_an_arbitrary_twin_but_folds_a_punctuation_variant()
    {
        // ⚠️ The model's suggestion names a NAME, and two products can share one (the duplicate guard has
        // a real "Add anyway"). A FirstOrDefault(string.Equals) — or the matcher's own First()-over-twins —
        // pre-selected an arbitrary twin whose count a confirm would overwrite. The identity resolver leaves
        // the (name-indistinguishable) dropdown for the human on twins, while still folding a punctuation
        // variant onto the ONE product it names.
        int variantId;
        using (var db = Db.CreateDbContext())
        {
            db.Products.AddRange(
                new Product { Name = "Whole Milk", Category = Category.Dairy },   // twin A
                new Product { Name = "Whole Milk", Category = Category.Dairy });  // twin B
            var variant = new Product { Name = "Half-and-Half", Category = Category.Dairy };
            db.Products.Add(variant);
            db.SaveChanges();
            variantId = variant.Id;
        }
        SeedPending("Walmart", Today.AddDays(-1),
            new ReceiptLine { RawText = "GV MILK", NormalizedName = "Milk", Quantity = 1m, SuggestedProduct = "Whole Milk" },
            new ReceiptLine { RawText = "GV HH", NormalizedName = "creamer", Quantity = 1m, SuggestedProduct = "Half and Half" });

        var cut = OpenReview();

        var selects = cut.FindAll("td select[aria-label^='Product match']");
        Assert.Equal("0", selects[0].GetAttribute("value"));                  // ambiguous twins → create-new, human picks
        Assert.Equal(variantId.ToString(), selects[1].GetAttribute("value")); // identity folds the punctuation variant
    }

    [Fact]
    public void A_twin_named_line_warns_and_annotates_the_twins_with_their_counts()
    {
        // ⚠️ Finding F. A receipt line whose name matches TWO products can't be auto-attached — the confirm
        // can't tell the twins apart, and left at create-new it would mint a THIRD zero-history twin. So it
        // is left unmatched, the grid WARNS, and the dropdown annotates the twins with their on-hand counts
        // so the household can tell them apart and pick — the same treatment the census grid gives twins.
        using (var db = Db.CreateDbContext())
        {
            db.Products.AddRange(
                new Product { Name = "Ground Beef", Category = Category.Meat, TrackQuantity = true, QuantityOnHand = 2m },
                new Product { Name = "Ground Beef", Category = Category.Meat, TrackQuantity = true, QuantityOnHand = 5m });
            db.SaveChanges();
        }
        SeedPending("Walmart", Today.AddDays(-1),
            new ReceiptLine { RawText = "GV BEEF", NormalizedName = "Ground Beef", Quantity = 1m, SuggestedProduct = "Ground Beef" });

        var cut = OpenReview();

        var row = cut.FindAll("tbody tr").Single();
        Assert.Equal("0", row.QuerySelector("select[aria-label^='Product match']")!.GetAttribute("value")); // not auto-attached
        Assert.Contains("More than one of your products answers", Collapsed(row));                           // the warning
        // The twin options carry their counts, so they aren't two identical "Ground Beef" entries.
        var options = row.QuerySelectorAll("select[aria-label^='Product match'] option").Select(o => o.TextContent).ToList();
        Assert.Contains(options, t => t.Contains("Ground Beef") && t.Contains("2 on hand"));
        Assert.Contains(options, t => t.Contains("Ground Beef") && t.Contains("5 on hand"));
    }

    [Fact]
    public void Renaming_a_twin_line_away_from_the_twin_name_drops_the_warning()
    {
        // ⚠️ Re-gate finding: the twin warning is a READ-TIME fact, so once the human RENAMES the line to a
        // distinct name it is stale — dropped via ShowTwinWarning, the same rename-suppression the census grid
        // gives its ambiguous-suggestion warning (finding C). Without it the warning kept claiming "more than
        // one of your products answers to this" about a name that no longer matches any.
        using (var db = Db.CreateDbContext())
        {
            db.Products.AddRange(
                new Product { Name = "Ground Beef", Category = Category.Meat, TrackQuantity = true, QuantityOnHand = 2m },
                new Product { Name = "Ground Beef", Category = Category.Meat, TrackQuantity = true, QuantityOnHand = 5m });
            db.SaveChanges();
        }
        SeedPending("Walmart", Today.AddDays(-1),
            new ReceiptLine { RawText = "GV BEEF", NormalizedName = "Ground Beef", Quantity = 1m, SuggestedProduct = "Ground Beef" });

        var cut = OpenReview();
        Assert.Contains("More than one of your products answers", Collapsed(cut.FindAll("tbody tr").Single())); // shown at first

        cut.FindAll("tbody tr").Single().QuerySelector("input[aria-label^='Item name']")!.Change("Ground Chuck");

        Assert.DoesNotContain("More than one of your products answers", Collapsed(cut.FindAll("tbody tr").Single()));
    }

    [Fact]
    public void An_undated_receipt_demands_a_date_and_a_dated_one_just_offers_correction()
    {
        SeedPending("Walmart", null, DbLine("A", "Thing"));
        var undated = OpenReview();
        Assert.Contains("No date detected", undated.Find(".review-date").TextContent);

        SeedPending("Target", Today.AddDays(-3), DbLine("B", "Other Thing"));
        var dated = RenderUpload();
        dated.WaitForState(() => dated.FindAll(".pending-receipts li").Count == 2);
        dated.FindAll(".pending-receipts li").Single(r => r.TextContent.Contains("Target"))
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Review").Click();
        dated.WaitForState(() => dated.FindAll(".review-actions").Count > 0);
        Assert.Contains("Detected from the receipt", dated.Find(".review-date").TextContent);
    }

    [Fact]
    public void Reviewing_an_exact_duplicate_warns_before_the_confirm_button_does_anything()
    {
        using (var db = Db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt
            {
                Merchant = "Walmart", PurchasedAt = Today.AddDays(-2), Status = ReceiptStatus.Confirmed,
                ImagePath = "n/a", Lines = [DbLine("GV MILK", "Whole Milk")],
            });
            db.SaveChanges();
        }
        SeedPending("Walmart", Today.AddDays(-2), DbLine("GV MILK", "Whole Milk"));

        var cut = OpenReview();

        Assert.Contains("exact duplicate", cut.Find("p.error[role=alert]").TextContent);
    }

    [Fact]
    public async Task Confirm_all_records_through_the_one_path_with_the_reviewed_date_and_assertion()
    {
        SeedPending("Walmart", Today.AddDays(-2),
            DbLine("GV MILK", "Whole Milk"), DbLine("BREAD", "Sandwich Bread"));
        var cut = OpenReview();

        cut.Find("#receipt-date").Change($"{Today.AddDays(-5):yyyy-MM-dd}"); // the human corrects the date
        cut.Find("input[aria-label^='I checked every line']").Change(true);
        cut.FindAll(".review-actions button").Single(b => b.TextContent.Trim() == "Confirm all").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Recorded 2 purchases (2 new products) from Walmart on " +
                $"{Today.AddDays(-5):yyyy-MM-dd}.", cut.Markup));
        Assert.Contains("Undo — remove this receipt", cut.Markup);

        await using var raw = Db.CreateUnscopedContext();
        var purchases = await raw.PurchaseEvents.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, purchases.Count);
        Assert.All(purchases, p => Assert.Equal(Today.AddDays(-5), p.PurchasedAt)); // the REVIEWED date
        var receipt = await raw.Receipts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(ReceiptStatus.Confirmed, receipt.Status);
        Assert.True(receipt.VerifiedForEval); // the human's explicit assertion rode through
    }

    [Fact]
    public async Task A_confirm_that_lost_the_race_reads_already_recorded_and_offers_no_undo()
    {
        var receiptId = SeedPending("Walmart", Today.AddDays(-2), DbLine("GV MILK", "Whole Milk"));
        var cut = OpenReview();

        // Another surface confirms the same receipt while this review sits open.
        var confirmer = Services.GetRequiredService<ReceiptConfirmationService>();
        await confirmer.ConfirmAsync(receiptId, Today.AddDays(-2),
            [new ReceiptConfirmationService.ConfirmLine("GV MILK", "Whole Milk", null, null, null, 1m, Category.Dairy, [], 0, null)],
            writeAliases: false);

        cut.FindAll(".review-actions button").Single(b => b.TextContent.Trim() == "Confirm all").Click();

        // Idempotent, and the panel says so — with NO Undo: this tap recorded nothing, so there is
        // nothing coherent for an undo to remove.
        cut.WaitForAssertion(() =>
            Assert.Contains("These purchases were already recorded.", cut.Markup));
        Assert.DoesNotContain("Undo — remove this receipt", cut.Markup);

        await using var raw = Db.CreateUnscopedContext();
        Assert.Single(await raw.PurchaseEvents.IgnoreQueryFilters().ToListAsync()); // once, not twice
    }

    [Fact]
    public async Task Confirming_on_the_page_records_an_undoable_ReceiptConfirmed_entry()
    {
        SeedPending("Walmart", Today.AddDays(-2), DbLine("GV MILK", "Whole Milk"));
        var cut = OpenReview();
        cut.FindAll(".review-actions button").Single(b => b.TextContent.Trim() == "Confirm all").Click();
        cut.WaitForState(() => cut.Markup.Contains("Undo — remove this receipt"));

        // The confirm and its undo record commit together — so /history can undo this receipt later, not
        // only the Done panel's ↩ Undo right here.
        await using var raw = Db.CreateUnscopedContext();
        var entry = Assert.Single(await raw.ActivityEntries.IgnoreQueryFilters()
            .Where(e => e.Kind == ActivityKind.ReceiptConfirmed).ToListAsync());
        Assert.Equal("Confirmed 1 item from Walmart", entry.Summary);
        Assert.Null(entry.UndoneAt);
    }

    [Fact]
    public async Task Undo_on_the_done_panel_reverses_the_confirm_it_is_about()
    {
        SeedPending("Walmart", Today.AddDays(-2), DbLine("GV MILK", "Whole Milk"));
        var cut = OpenReview();
        cut.FindAll(".review-actions button").Single(b => b.TextContent.Trim() == "Confirm all").Click();
        cut.WaitForState(() => cut.Markup.Contains("Undo — remove this receipt"));

        // The checkbox wasn't ticked, so the confirm must NOT have asserted ground truth — the
        // other side of the verified-for-eval trust boundary the Confirm_all test ticks.
        await using (var check = Db.CreateUnscopedContext())
        {
            Assert.False((await check.Receipts.IgnoreQueryFilters().SingleAsync()).VerifiedForEval);
        }

        cut.FindAll("button").Single(b => b.TextContent.Contains("Undo — remove this receipt")).Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Undone — the receipt and its 1 purchase were removed.", cut.Markup));
        Assert.DoesNotContain("Undo — remove this receipt", cut.Markup); // one undo per confirm

        await using var raw = Db.CreateUnscopedContext();
        Assert.Empty(await raw.PurchaseEvents.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await raw.Receipts.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Discard_from_review_parks_the_receipt_and_returns_to_idle()
    {
        var receiptId = SeedPending("Walmart", Today.AddDays(-2), DbLine("GV MILK", "Whole Milk"));
        var cut = OpenReview();

        cut.FindAll(".review-actions button").Single(b => b.TextContent.Trim() == "Discard receipt").Click();

        cut.WaitForAssertion(() => Assert.Contains("Upload receipt", cut.Find("h1").TextContent));
        Assert.Empty(cut.FindAll(".pending-receipts li")); // discarded rows leave the queue

        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(ReceiptStatus.Discarded,
            (await raw.Receipts.IgnoreQueryFilters().SingleAsync(r => r.Id == receiptId)).Status);
    }

    [Fact]
    public async Task Retry_reextracts_a_failed_receipt_from_its_audit_copy()
    {
        // The audit copy is the way back for a transient API blip — without it the file was parked
        // forever with the server logs as the only clue.
        var imagePath = await Storage.NewFolderAsync();
        await Storage.WritePageAsync(imagePath, 0, [1, 2, 3], "image/png");
        int receiptId;
        using (var db = Db.CreateDbContext())
        {
            var receipt = new Receipt { Merchant = null, Status = ReceiptStatus.PendingReview, ImagePath = imagePath };
            db.Receipts.Add(receipt);
            db.SaveChanges();
            receiptId = receipt.Id;
        }
        Extractor.Results.Enqueue(new ExtractionResult { Success = false, Error = "blurry", RawModelJson = "{}" });
        var cut = RenderUpload();
        cut.WaitForState(() => cut.FindAll(".pending-receipts li").Count == 1);

        cut.FindAll(".pending-receipts button").Single(b => b.TextContent.Trim() == "Retry").Click();
        cut.WaitForAssertion(() =>
            Assert.Contains("Still couldn't read it. (blurry)", cut.Find("p.error").TextContent));

        // Second retry succeeds and lands straight in the normal review flow.
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Walmart", PurchaseDate = Today.AddDays(-1), Lines = [Line("GV MILK", "Whole Milk")],
        }, "{}"));
        cut.FindAll(".pending-receipts button").Single(b => b.TextContent.Trim() == "Retry").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll(".review-actions"));
            Assert.Contains("Whole Milk", cut.Find("input[aria-label^='Item name']").GetAttribute("value"));
        });
        await using var raw = Db.CreateUnscopedContext();
        Assert.Single(await raw.ReceiptLines.IgnoreQueryFilters().Where(l => l.ReceiptId == receiptId).ToListAsync());
    }

    [Fact]
    public async Task The_expires_column_exists_only_for_households_tracking_dates()
    {
        SeedPending("Walmart", Today.AddDays(-2), DbLine("GV MILK", "Whole Milk"));
        var off = OpenReview();
        Assert.DoesNotContain(off.FindAll("th").Select(h => h.TextContent.Trim()), t => t == "Expires");

        await AppSettings.SetAsync(SettingKeys.TrackExpirationDates, "true");
        var on = OpenReview(RenderUpload());
        Assert.Contains(on.FindAll("th").Select(h => h.TextContent.Trim()), t => t == "Expires");
    }

    // ------------------------------------------------------------------------------ tag dedup UI

    [Fact]
    public void A_near_duplicate_tag_is_questioned_and_use_takes_the_canonical_form()
    {
        // A tag the household coined itself (not in the seed vocabulary), so the canonical form
        // under test is unambiguously theirs.
        using (var db = Db.CreateDbContext())
        {
            var p = new Product { Name = "Ketchup", Category = Category.Pantry, Tags = [new ProductTag { Value = "weeknight" }] };
            db.Products.Add(p);
            db.SaveChanges();
        }
        SeedPending("Walmart", Today.AddDays(-2), DbLine("MUSTARD", "Mustard"));
        var cut = OpenReview();

        cut.Find("input[aria-label^='Add a tag']").Input("Weeknights");
        cut.FindAll(".tag-add button").Single(b => b.TextContent.Trim() == "Add").Click();

        // Stage 1 (plain code): a casing/plural variant must ask, not fragment the cloud.
        cut.WaitForAssertion(() => Assert.Contains("looks like", cut.Find(".tag-warning").TextContent));
        cut.FindAll(".tag-warning button").Single(b => b.TextContent.StartsWith("Use")).Click();

        cut.WaitForAssertion(() =>
        {
            var chips = cut.FindAll(".tags-cell .tag-chip").Select(c => c.TextContent.TrimEnd('×').Trim());
            Assert.Contains("weeknight", chips); // the CANONICAL form, not the near-dupe
        });
        Assert.Empty(cut.FindAll(".tag-warning"));
    }

    [Fact]
    public void The_synonym_check_asks_second_and_add_anyway_keeps_the_users_word()
    {
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product { Name = "Ketchup", Category = Category.Pantry, Tags = [new ProductTag { Value = "condiment" }] });
            db.SaveChanges();
        }
        SeedPending("Walmart", Today.AddDays(-2), DbLine("MUSTARD", "Mustard"));
        TagAdvisor.Synonym = "condiment"; // the model thinks "topping" means condiment
        var cut = OpenReview();

        cut.Find("input[aria-label^='Add a tag']").Input("topping");
        cut.FindAll(".tag-add button").Single(b => b.TextContent.Trim() == "Add").Click();

        cut.WaitForAssertion(() => Assert.Contains("looks like", cut.Find(".tag-warning").TextContent));
        cut.FindAll(".tag-warning button").Single(b => b.TextContent.Contains("anyway")).Click();

        // The user can always overrule the dedup — their word, verbatim.
        cut.WaitForAssertion(() =>
        {
            var chips = cut.FindAll(".tags-cell .tag-chip").Select(c => c.TextContent.TrimEnd('×').Trim());
            Assert.Contains("topping", chips);
        });
    }

    // ------------------------------------------ extract delivery (the module POSTs; .NET routes the result)

    // photo-upload.js resizes + holds the picked bytes, renders the staged list + Extract button ITSELF
    // (client-side, so a dropped mobile circuit can't hide them), POSTs off-circuit, and hands the page only
    // the finished outcome(s) via OnExtracted — retried across a reconnect. bUnit can't run that JS, so these
    // call OnExtracted directly with the results the module would deliver; the staging UI, the POST, and the
    // combine/double-tap guards are JS, live-verified on the dev server.
    private static Upload.UploadResult Extracted(int receiptId, bool autoConfirmed, string? merchant = "Walmart",
        int purchases = 0, int lineCount = 1, string name = "receipt.jpg") => new()
    {
        Ok = true,
        Name = name,
        Outcome = new Upload.UploadOutcome
        {
            ReceiptId = receiptId, Success = true, AutoConfirmed = autoConfirmed, Purchases = purchases,
            Merchant = merchant, PurchasedAt = Today.AddDays(-1), LineCount = lineCount,
        },
    };

    private Task Deliver(IRenderedComponent<Upload> cut, bool combined, params Upload.UploadResult[] results) =>
        cut.InvokeAsync(() => cut.Instance.OnExtracted(results, combined, results.Length));

    [Fact]
    public async Task A_trusted_upload_auto_confirms_and_says_it_did()
    {
        var cut = RenderUpload();
        await Deliver(cut, combined: true, Extracted(receiptId: 1, autoConfirmed: true, purchases: 1));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Recorded 1 purchases from Walmart", cut.Markup);
            Assert.Contains("Confirmed automatically", cut.Markup);
        });
    }

    [Fact]
    public async Task An_untrusted_upload_lands_in_the_review_grid()
    {
        // The router wouldn't vouch for it, so the page loads the persisted receipt into review — the
        // ReceiptId the endpoint returned is real (the ingest already saved it).
        var receiptId = SeedPending("Walmart", Today.AddDays(-1), DbLine("ZZZ", "Completely Novel Thing"));
        var cut = RenderUpload();
        await Deliver(cut, combined: true, Extracted(receiptId, autoConfirmed: false));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".review-actions")));
        Assert.Equal("Completely Novel Thing", cut.Find("input[aria-label^='Item name']").GetAttribute("value"));
    }

    [Fact]
    public async Task A_failed_read_says_so_and_keeps_the_image_for_review()
    {
        var cut = RenderUpload();
        await Deliver(cut, combined: true, new Upload.UploadResult
        {
            Ok = true, Name = "receipt.jpg",
            Outcome = new Upload.UploadOutcome { ReceiptId = 1, Success = false, Error = "blurry" },
        });

        cut.WaitForAssertion(() => Assert.Contains("couldn't be read", Collapsed(cut.Find("p.error").TextContent)));
    }

    [Fact]
    public async Task A_request_level_upload_failure_is_surfaced()
    {
        // A non-200 from the endpoint (too large, session expired) comes back as Ok=false with the
        // endpoint's own message; the page shows it rather than a generic error.
        var cut = RenderUpload();
        await Deliver(cut, combined: true, new Upload.UploadResult { Ok = false, Error = "That upload was too large." });

        cut.WaitForAssertion(() => Assert.Contains("That upload was too large.", cut.Find("p.error").TextContent));
    }

    [Fact]
    public async Task A_combined_delivery_opens_review_rather_than_the_batch_readout()
    {
        // combined:true → HandleCombined loads the single receipt into review. The module decides combined
        // vs batch from the "one receipt" tick (that decision is JS); the page just routes on the flag.
        var receiptId = SeedPending("Costco", Today.AddDays(-1), DbLine("LONG", "Bulk Thing"));
        var cut = RenderUpload();
        await Deliver(cut, combined: true, Extracted(receiptId, autoConfirmed: false));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".review-actions")));
        Assert.Empty(cut.FindAll(".batch-progress")); // the combined path, not the batch readout
    }

    [Fact]
    public async Task A_batch_delivery_lands_each_receipt_in_the_queue_readout()
    {
        // combined:false → the module POSTed each file on its own; every result shows in the batch readout
        // (by file name) and lands in the review queue.
        var cut = RenderUpload();
        await Deliver(cut, combined: false,
            Extracted(receiptId: 1, autoConfirmed: false, merchant: "M", name: "one.jpg"),
            Extracted(receiptId: 2, autoConfirmed: false, merchant: "N", name: "two.jpg"));

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll(".batch-progress li");
            Assert.Equal(2, items.Count);
            Assert.All(items, li => Assert.Contains("in the review queue below", li.TextContent));
        });
        Assert.Contains("one.jpg", cut.Markup);
        Assert.Contains("two.jpg", cut.Markup);
    }

    [Fact]
    public async Task A_delivered_error_does_not_hide_the_review_queue()
    {
        // A request-level failure reaches Phase.Error; hiding the queued receipts behind it would read as
        // them being lost.
        SeedPending("Walmart", Today.AddDays(-2), DbLine("GV MILK", "Whole Milk"));
        var cut = RenderUpload();

        await Deliver(cut, combined: true, new Upload.UploadResult { Ok = false, Error = "That upload was too large." });

        cut.WaitForAssertion(() => Assert.Contains("That upload was too large.", cut.Find("p.error").TextContent));
        Assert.Contains("Waiting for review (1)", cut.Markup); // the queue survived the error
    }

    // -------------------------------------------------------------------------- the file picker markup

    [Fact]
    public void There_is_one_unfiltered_picker_and_no_camera_capture_button()
    {
        // ⚠️ No `accept`: a mixed image+PDF list greys HEIC photos out of the iOS picker (the default
        // iPhone format), so the one control that takes a photo OR a PDF filters by nothing; the module
        // resizes whatever the browser can decode and takes PDFs as-is. A plain <input> drives the JS
        // uploader — NOT Blazor's <InputFile> — and photo-upload.js renders the staged list + Extract button
        // beside it, so the picked bytes AND the staging UI are client-side and a mobile circuit blip during
        // the picker can't lose them.
        var cut = RenderUpload();

        var inputs = cut.FindAll("input[type=file]");
        Assert.Single(inputs);                          // one control, no separate snap input
        Assert.Null(inputs[0].GetAttribute("accept"));  // unfiltered, so HEIC + PDF both come through
        Assert.Null(inputs[0].GetAttribute("capture")); // no capture — it dropped the circuit on Android
    }
}
