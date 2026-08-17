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
    internal StubPhotoLoader PhotoLoader = null!;

    internal sealed class QueueExtractor : IReceiptExtractor
    {
        public Queue<ExtractionResult> Results { get; } = new();
        public List<int> PageCounts { get; } = [];
        /// <summary>Media type of every page ever handed over — how a test proves a photo reached
        /// extraction as the loader's JPEG rather than as its raw picked bytes.</summary>
        public List<string> MediaTypes { get; } = [];

        public Task<ExtractionResult> ExtractAsync(
            IReadOnlyList<ReceiptAttachment> pages, IReadOnlyList<string>? knownProductNames = null,
            IReadOnlyList<string>? knownTags = null, CancellationToken cancellationToken = default)
        {
            PageCounts.Add(pages.Count);
            MediaTypes.AddRange(pages.Select(p => p.MediaType));
            return Task.FromResult(Results.Count > 0
                ? Results.Dequeue()
                : new ExtractionResult { Success = false, Error = "no result queued", RawModelJson = "{}" });
        }
    }

    internal sealed class FakeTagAdvisor : ITagAdvisor
    {
        public string? Synonym { get; set; }
        public Task<string?> FindSynonymAsync(string candidate, IReadOnlyList<string> existing, CancellationToken ct = default) =>
            Task.FromResult(Synonym);
    }

    protected override void RegisterAdditionalServices()
    {
        var household = new FakeCurrentHousehold("hh-test");
        Storage = new ReceiptStorage(
            new AppPaths(dataDir, Path.Combine(dataDir, "receipts")), household, NullLogger<ReceiptStorage>.Instance);
        Extractor = new QueueExtractor();
        TagAdvisor = new FakeTagAdvisor();
        PhotoLoader = new StubPhotoLoader();
        var confirmer = new ReceiptConfirmationService(Factory);
        var duplicates = new ReceiptDuplicateDetector(Factory);
        Services.AddSingleton<IReceiptExtractor>(Extractor);
        Services.AddSingleton<ITagAdvisor>(TagAdvisor);
        Services.AddSingleton<Web.Services.IShelfPhotoLoader>(PhotoLoader);
        Services.AddSingleton(Storage);
        Services.AddSingleton(confirmer);
        Services.AddSingleton(duplicates);
        Services.AddSingleton(new ReceiptAutoConfirmer(Factory, AppSettings, confirmer, duplicates,
            NullLogger<ReceiptAutoConfirmer>.Instance));
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
        // Only the alias row wears the "learned from this merchant" link mark.
        Assert.Single(cut.FindAll("[title='Matched a previous receipt line from this merchant']"));
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

    // ------------------------------------------------------------------- the file-upload pipeline

    private static InputFileContent Pdf(string name) =>
        InputFileContent.CreateFromBinary([0x25, 0x50, 0x44, 0x46], name, contentType: "application/pdf");

    [Fact]
    public async Task A_trusted_single_upload_smart_confirms_and_says_it_did()
    {
        // Smart's contract: every line resolves to something already bought → record it, say so,
        // and keep review out of the way. The line matches the existing product exactly.
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Whole Milk", Category = Category.Dairy,
                Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-10), Quantity = 1m }],
            });
            db.SaveChanges();
        }
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Walmart", PurchaseDate = Today.AddDays(-1), Lines = [Line("GV MILK", "Whole Milk")],
        }, "{}"));
        var cut = RenderUpload();

        cut.FindComponent<InputFile>().UploadFiles(Pdf("receipt.pdf"));
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Extract"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Extract").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Recorded 1 purchases from Walmart", cut.Markup);
            Assert.Contains("Confirmed automatically", cut.Markup);
        });

        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(2, await raw.PurchaseEvents.IgnoreQueryFilters().CountAsync()); // the seed + this one
        Assert.Equal(ReceiptStatus.Confirmed, (await raw.Receipts.IgnoreQueryFilters().SingleAsync()).Status);
        // The audit copy landed on disk before extraction — the Retry path's lifeline.
        var receipt = await raw.Receipts.IgnoreQueryFilters().SingleAsync();
        Assert.True(Storage.HasPages(receipt.ImagePath));
    }

    [Fact]
    public async Task An_untrusted_single_upload_falls_through_to_review()
    {
        // A brand-new product: Smart must NOT invent it silently — the review grid is the gate.
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Walmart", PurchaseDate = Today.AddDays(-1), Lines = [Line("ZZZ", "Completely Novel Thing")],
        }, "{}"));
        var cut = RenderUpload();

        cut.FindComponent<InputFile>().UploadFiles(Pdf("receipt.pdf"));
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Extract"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Extract").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".review-actions")));
        Assert.Equal("Completely Novel Thing", cut.Find("input[aria-label^='Item name']").GetAttribute("value"));

        await using var raw = Db.CreateUnscopedContext();
        Assert.Empty(await raw.PurchaseEvents.IgnoreQueryFilters().ToListAsync()); // nothing recorded yet
    }

    [Fact]
    public async Task A_stack_of_files_reads_as_separate_receipts_with_a_per_file_readout()
    {
        await AppSettings.SetAsync(SettingKeys.ImportMode, "Review");
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Walmart", PurchaseDate = Today.AddDays(-2), Lines = [Line("A", "Thing One")],
        }, "{}"));
        Extractor.Results.Enqueue(new ExtractionResult { Success = false, Error = "blurry", RawModelJson = "{}" });
        var cut = RenderUpload();

        cut.FindComponent<InputFile>().UploadFiles(Pdf("one.pdf"), Pdf("two.pdf"));
        cut.WaitForState(() => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Extract"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Extract").Click();

        // Each file is its own receipt with its own fate: one queued for review, one failed but
        // KEPT (audit copy + queue row with Retry) — one bad file must not sink the stack.
        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll(".batch-progress li");
            Assert.Equal(2, items.Count);
            Assert.Contains("in the review queue below", items[0].TextContent);
            Assert.Contains("couldn't be read — retry it from the queue below", items[1].TextContent);
        });
        // The batch's finally block refreshes the queue after the readout settles.
        cut.WaitForAssertion(() => Assert.Contains("Waiting for review (2)", cut.Markup));
        Assert.Equal([1, 1], Extractor.PageCounts); // extracted one at a time, page each
    }

    [Fact]
    public void Combining_pages_extracts_one_receipt_from_many_files()
    {
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Costco", PurchaseDate = Today.AddDays(-1), Lines = [Line("LONG", "Novel Bulk Thing")],
        }, "{}"));
        var cut = RenderUpload();

        cut.FindComponent<InputFile>().UploadFiles(Pdf("page1.pdf"), Pdf("page2.pdf"));
        cut.WaitForState(() => cut.FindAll(".combine-check input").Count == 1);
        cut.Find(".combine-check input").Change(true);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Extract").Click();

        // One extraction call carrying BOTH pages — the one-long-receipt case, not two receipts.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".review-actions")));
        Assert.Equal([2], Extractor.PageCounts);
    }

    // -------------------------------------------------------- the file picker and the staged list

    [Fact]
    public void There_is_one_unfiltered_picker_and_no_camera_capture_button()
    {
        // ⚠️ No `accept`: a mixed image+PDF list greys HEIC photos out of the iOS picker (the default
        // iPhone format), so the one control that takes a photo OR a PDF filters by nothing and lets
        // the loader refuse non-photos. The camera-capture button was removed — `capture` drops the
        // Blazor circuit on Android and never opened the camera directly anyway; the picker still
        // offers the device camera through the OS sheet.
        var cut = RenderUpload();

        var inputs = cut.FindAll("input[type=file]");
        Assert.Single(inputs);                              // one control, no separate snap input
        Assert.Null(inputs[0].GetAttribute("accept"));      // unfiltered, so HEIC + PDF both come through
        Assert.Null(inputs[0].GetAttribute("capture"));     // no capture — see the removal above
        Assert.Empty(cut.FindAll("label.file-button"));     // and no proxied camera button
    }

    [Fact]
    public void A_pdf_is_staged_without_touching_the_photo_loader()
    {
        // PDFs pass through raw — the loader is for photos, and handing it a PDF would time out
        // against a canvas that can't decode one.
        var cut = RenderUpload();

        cut.FindComponent<InputFile>().UploadFiles(Pdf("order.pdf"));

        Assert.Contains("1 file(s) selected", cut.Markup);
        Assert.Empty(PhotoLoader.Loaded);
    }

    [Fact]
    public void A_photo_is_staged_through_the_shared_loader_and_extracted_as_jpeg()
    {
        // The receipt IMAGE path was untestable before the loader seam (PDFs skip the resize, so
        // bUnit could only ever drive PDFs through extraction). Now the stub stands in for the
        // browser downscale exactly as on the census page — and what reaches the extractor must be
        // the loader's JPEG, not the raw picked bytes.
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Walmart", PurchaseDate = Today.AddDays(-1), Lines = [Line("GV MILK", "Whole Milk")],
        }, "{}"));
        var cut = RenderUpload();

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([9, 9, 9], "IMG_0001.jpg", contentType: "image/jpeg"));
        Assert.Equal(["IMG_0001.jpg"], PhotoLoader.Loaded);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Extract").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".review-actions")));
        Assert.Equal(["image/jpeg"], Extractor.MediaTypes);
    }

    [Fact]
    public void Picks_append_across_change_events_and_a_staged_file_can_be_removed()
    {
        // Each change event ADDS to the staged list (a second pick must not throw away the first);
        // the ✕ on a staged row is the way back out.
        var cut = RenderUpload();

        cut.FindComponent<InputFile>().UploadFiles(Pdf("a.pdf"));
        cut.FindComponent<InputFile>().UploadFiles(Pdf("b.pdf"));
        Assert.Contains("2 file(s) selected", cut.Markup);

        cut.FindAll(".staged-files button")
            .Single(b => b.GetAttribute("aria-label") == "Remove a.pdf").Click();

        Assert.Contains("1 file(s) selected", cut.Markup);
        Assert.Contains("b.pdf", cut.Markup);
        Assert.DoesNotContain("a.pdf", cut.Markup);
    }

    [Fact]
    public async Task A_photo_lands_beside_a_picked_pdf_and_extracts_as_a_batch()
    {
        // A PDF and a photo, picked across two selections, feed the same staged list — one model.
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Walmart", PurchaseDate = Today.AddDays(-1), Lines = [Line("GV MILK", "Whole Milk")],
        }, "{}"));
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Target", PurchaseDate = Today.AddDays(-1), Lines = [Line("EGGS", "Eggs")],
        }, "{}"));
        var cut = RenderUpload();

        cut.FindComponent<InputFile>().UploadFiles(Pdf("a.pdf"));
        await FileEvents.ChangeAsync(cut, 0, "image.jpg");  // a second pick — a photo

        Assert.Contains("2 file(s) selected", cut.Markup);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Extract").Click();

        // Two files, combine unticked → two receipts, extracted one at a time.
        cut.WaitForAssertion(() => Assert.Equal([1, 1], Extractor.PageCounts));
    }

    [Fact]
    public void Appending_past_the_file_cap_is_refused_and_keeps_what_is_staged()
    {
        var cut = RenderUpload();
        cut.FindComponent<InputFile>().UploadFiles(
            [.. Enumerable.Range(0, 20).Select(i => Pdf($"r{i}.pdf"))]);

        cut.FindComponent<InputFile>().UploadFiles(Pdf("one-too-many.pdf"));

        Assert.Contains("please keep it to 20 per upload", cut.Markup);
        Assert.Contains("20 file(s) selected", cut.Markup);  // nothing was dropped
    }

    [Fact]
    public void A_bad_file_is_named_and_does_not_unstage_its_neighbours()
    {
        PhotoLoader.ThrowsOnce = new InvalidOperationException("the browser could not decode this image");
        var cut = RenderUpload();

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1], "broken.jpg", contentType: "image/jpeg"),
            InputFileContent.CreateFromBinary([2], "fine.jpg", contentType: "image/jpeg"));

        Assert.Contains("“broken.jpg” couldn't be read", Collapsed(cut.Markup));
        Assert.Contains("1 file(s) selected", cut.Markup);
        Assert.Contains("fine.jpg", cut.Markup);
    }

    [Fact]
    public void Emptying_the_staged_list_clears_the_one_receipt_tick()
    {
        // Appending keeps the tick on purpose (adding pages to one long receipt is the flagship
        // flow), so an empty list is the one fresh-start boundary left — without the clear, a tick
        // made for files that were all ✕'d away silently merged the NEXT, unrelated pick into one
        // receipt.
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Walmart", PurchaseDate = Today.AddDays(-1), Lines = [Line("GV MILK", "Whole Milk")],
        }, "{}"));
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Target", PurchaseDate = Today.AddDays(-1), Lines = [Line("EGGS", "Eggs")],
        }, "{}"));
        var cut = RenderUpload();
        cut.FindComponent<InputFile>().UploadFiles(Pdf("a.pdf"), Pdf("b.pdf"));
        cut.Find(".combine-check input").Change(true);

        cut.FindAll(".staged-files button").First().Click();
        cut.FindAll(".staged-files button").First().Click();
        Assert.DoesNotContain("file(s) selected", cut.Markup);

        cut.FindComponent<InputFile>().UploadFiles(Pdf("c.pdf"), Pdf("d.pdf"));
        Assert.False(cut.Find(".combine-check input").HasAttribute("checked")); // fresh batch, fresh tick

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Extract").Click();
        cut.WaitForAssertion(() => Assert.Equal([1, 1], Extractor.PageCounts)); // two receipts, not one merged
    }

    [Fact]
    public void An_ingest_error_does_not_hide_the_review_queue()
    {
        // Reads happen at selection now, so Phase.Error is one wrong file away — and hiding the
        // queued receipts behind it read as them being lost.
        SeedPending("Walmart", Today.AddDays(-2), DbLine("GV MILK", "Whole Milk"));
        PhotoLoader.Throws = new NotSupportedException(
            "“notes.txt” is text/plain, which isn't a photo. Take or pick a picture instead.");
        var cut = RenderUpload();
        cut.WaitForAssertion(() => Assert.Contains("Waiting for review (1)", cut.Markup));

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1], "notes.txt", contentType: "text/plain"));

        cut.WaitForAssertion(() => Assert.Contains("isn't a photo", cut.Markup));
        Assert.Contains("Waiting for review (1)", cut.Markup); // the queue survived the error
    }

    [Fact]
    public async Task A_pick_during_a_stuck_ingest_is_told_to_wait_not_silently_dropped()
    {
        // The guard is right to DROP the second event (its handles reference the input the
        // finished ingest replaces) — but dropping without a word is the tap-that-looks-ignored
        // class. The note also leaves with the ingest that made it true.
        var cut = RenderUpload();
        var gate = new TaskCompletionSource();
        PhotoLoader.Hold = gate;
        var ingest = FileEvents.ChangeAsync(cut, 0, "slow.jpg");

        await FileEvents.ChangeAsync(cut, 0, "eager.jpg"); // arrives mid-ingest

        cut.WaitForAssertion(() => Assert.Contains("Still reading your last pick", cut.Markup));
        gate.SetResult();
        await ingest;
        cut.WaitForAssertion(() => Assert.DoesNotContain("Still reading your last pick", cut.Markup));
        Assert.Contains("slow.jpg", cut.Markup);        // the first pick landed
        Assert.DoesNotContain("eager.jpg", cut.Markup); // the second was refused, and said so
    }

    [Fact]
    public async Task Extract_stands_down_while_a_file_is_still_being_read_in()
    {
        // An extract must not snapshot a staged list an ingest is still appending to — and a queued
        // second Extract click must not run a second extraction (each run persists a receipt, so two
        // runs would record the same staged list twice).
        Extractor.Results.Enqueue(ExtractionResult.Ok(new ExtractedReceipt
        {
            Merchant = "Walmart", PurchaseDate = Today.AddDays(-1), Lines = [Line("GV MILK", "Whole Milk")],
        }, "{}"));
        var cut = RenderUpload();
        cut.FindComponent<InputFile>().UploadFiles(Pdf("a.pdf"));

        var gate = new TaskCompletionSource();
        PhotoLoader.Hold = gate;
        var ingest = FileEvents.ChangeAsync(cut, 0, "image.jpg");  // parks mid-ingest

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Extract").Click();  // must NO-OP
        Assert.Empty(Extractor.PageCounts);

        gate.SetResult();
        await ingest;
        Assert.Contains("2 file(s) selected", cut.Markup);
    }

    [Fact]
    public async Task A_refused_append_still_hands_back_fresh_inputs()
    {
        // The cap refusal's recovery ("Extract these first, or remove one") ends in another pick,
        // often re-picking the same file name (image.jpg), which fires no change event unless the
        // element is FRESH (the @key rule). Element identity is the observable half; the browser's
        // same-name suppression itself can't run under bUnit.
        var cut = RenderUpload();
        cut.FindComponent<InputFile>().UploadFiles(
            [.. Enumerable.Range(0, 20).Select(i => Pdf($"r{i}.pdf"))]);
        var before = cut.FindComponent<InputFile>().Instance;

        await FileEvents.ChangeAsync(cut, 0, "image.jpg"); // would be 21 — refused

        Assert.Contains("please keep it to 20 per upload", cut.Markup);
        Assert.NotSame(before, cut.FindComponent<InputFile>().Instance);
    }

    [Fact]
    public void An_overlarge_selection_still_hands_back_fresh_inputs()
    {
        // Same rule on the >MaxFiles-in-one-go refusal: the overlarge pick is the element's value
        // now, and a corrected re-pick overlapping the same names must fire.
        var cut = RenderUpload();
        var before = cut.FindComponents<InputFile>()[0].Instance;

        cut.FindComponent<InputFile>().UploadFiles(
            [.. Enumerable.Range(0, 21).Select(i => Pdf($"r{i}.pdf"))]);

        cut.WaitForAssertion(() => Assert.Contains("up to 20 files", cut.Markup));
        Assert.NotSame(before, cut.FindComponents<InputFile>()[0].Instance);
    }
}
