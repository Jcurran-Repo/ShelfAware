using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Tracking a confirmed receipt line that points at no product — the /receipts "Create new product"
/// callout. The receipts here are confirmed through the ONE real confirm path with a line left out (the
/// review grid's ✕), so the line under test is exactly what a household actually ends up with.
/// </summary>
public class ReceiptLineProductServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-web-tests", Guid.NewGuid().ToString("N"));
    private readonly ReceiptLineProductService _service;

    private static readonly DateOnly Bought = new(2026, 7, 1);

    public ReceiptLineProductServiceTests() => _service = new ReceiptLineProductService(_db);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }

    // --- helpers -------------------------------------------------------------

    private async Task<Product> SeedProduct(string name, Action<Product>? configure = null)
    {
        await using var db = _db.CreateDbContext();
        var product = new Product { Name = name };
        configure?.Invoke(product);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    /// <summary>A receipt with a kept "Whole Milk" line and the given <paramref name="removed"/> line,
    /// confirmed with the removed line ✕'d out of review. Returns the removed line's id.</summary>
    private async Task<(int ReceiptId, int LineId)> ConfirmWithOneLineRemoved(ReceiptLine removed)
    {
        int receiptId;
        await using (var db = _db.CreateDbContext())
        {
            var receipt = new Receipt
            {
                Merchant = "Walmart",
                ImagePath = "line-product-test",
                Lines = [new ReceiptLine { RawText = "GV MILK", NormalizedName = "Whole Milk" }, removed],
            };
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync();
            receiptId = receipt.Id;
        }
        await new ReceiptConfirmationService(_db, UndoTesting.Log(_db)).ConfirmAsync(receiptId, Bought,
            [new("GV MILK", "Whole Milk", null, null, null, 1, Category.Dairy, [], 0)], writeAliases: true);
        return (receiptId, removed.Id);
    }

    private static ReceiptLine Line(string name, string raw = "RAW LINE") =>
        new() { RawText = raw, NormalizedName = name, Quantity = 1 };

    // --- creating ------------------------------------------------------------

    [Fact]
    public async Task A_removed_line_becomes_a_product_with_its_purchase_on_the_receipts_date()
    {
        var removed = Line("Dragonfruit Salsa", "DRAGON SALSA");
        removed.Quantity = 2;
        removed.Brand = "Frontera";
        removed.Size = "16 oz";
        removed.Variety = "Mild";
        removed.Category = Category.Pantry;
        removed.TagsJson = ReceiptConfirmationService.SerializeTags(["spicy"]);
        var (receiptId, lineId) = await ConfirmWithOneLineRemoved(removed);

        var outcome = await _service.TrackAsync(lineId);

        Assert.Equal(ReceiptLineProductService.Status.Created, outcome.Status);
        Assert.Equal("Dragonfruit Salsa", outcome.ProductName);

        await using var db = _db.CreateDbContext();
        var salsa = await db.Products.Include(p => p.Tags).SingleAsync(p => p.Name == "Dragonfruit Salsa");
        Assert.Equal(outcome.ProductId, salsa.Id);
        Assert.Equal(Category.Pantry, salsa.Category);
        Assert.Equal(receiptId, salsa.CreatedByReceiptId); // the receipt introduced it
        // The reviewed tag, canonicalized against the household vocabulary exactly as a confirm would.
        Assert.Equal(["Spice"], salsa.Tags.Select(t => t.Value));

        var buy = await db.PurchaseEvents.SingleAsync(p => p.ProductId == salsa.Id);
        Assert.Equal(Bought, buy.PurchasedAt);
        Assert.Equal(2m, buy.Quantity);
        Assert.Equal("Frontera", buy.Brand);
        Assert.Equal("16 oz", buy.Size);
        Assert.Equal("Mild", buy.Variety);
        Assert.Equal(PurchaseSource.Receipt, buy.Source);
        Assert.Equal(receiptId, buy.ReceiptId); // provenance: removing the receipt unwinds it

        Assert.Equal(salsa.Id, (await db.ReceiptLines.SingleAsync(l => l.Id == lineId)).ProductId);
    }

    [Fact]
    public async Task An_undefined_stored_category_is_created_as_Other()
    {
        var removed = Line("Mystery Jar");
        removed.Category = (Category)999;
        var (_, lineId) = await ConfirmWithOneLineRemoved(removed);

        await _service.TrackAsync(lineId);

        await using var db = _db.CreateDbContext();
        Assert.Equal(Category.Other, (await db.Products.SingleAsync(p => p.Name == "Mystery Jar")).Category);
    }

    // --- linking instead of duplicating --------------------------------------

    [Fact]
    public async Task A_line_whose_name_is_an_existing_products_identity_links_rather_than_duplicating()
    {
        // Punctuation folds away under the matcher's identity — "Home Canned" IS "Home-Canned".
        var sauce = await SeedProduct("Home-Canned Sauce");
        var (_, lineId) = await ConfirmWithOneLineRemoved(Line("Home Canned Sauce"));

        var outcome = await _service.TrackAsync(lineId);

        Assert.Equal(ReceiptLineProductService.Status.Linked, outcome.Status);
        Assert.Equal(sauce.Id, outcome.ProductId);
        Assert.Equal("Home-Canned Sauce", outcome.ProductName);
        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Products.CountAsync(p => p.Name.Contains("Sauce")));
        Assert.Equal(sauce.Id, (await db.ReceiptLines.SingleAsync(l => l.Id == lineId)).ProductId);
        Assert.Equal(1, await db.PurchaseEvents.CountAsync(p => p.ProductId == sauce.Id));
    }

    [Fact]
    public async Task A_fuzzy_similarity_is_not_an_identity_and_creates_its_own_product()
    {
        // "Milk" is a substring of "Whole Milk" — a guess, and a guess must not decide whose history
        // a purchase joins. (The kept line already created Whole Milk.)
        var (_, lineId) = await ConfirmWithOneLineRemoved(Line("Milk"));

        var outcome = await _service.TrackAsync(lineId);

        Assert.Equal(ReceiptLineProductService.Status.Created, outcome.Status);
        await using var db = _db.CreateDbContext();
        Assert.Equal(["Milk", "Whole Milk"], await db.Products.Select(p => p.Name).OrderBy(n => n).ToListAsync());
    }

    [Fact]
    public async Task Linking_retracks_an_untracked_product_and_moves_its_count()
    {
        var eggs = await SeedProduct("Eggs", p =>
        {
            p.IsTracked = false;
            p.TrackQuantity = true;
            p.QuantityOnHand = 3;
            p.QuantityCountedAt = DateTimeOffset.Now.AddDays(-30);
        });
        var removed = Line("Eggs");
        removed.Quantity = 2;
        var (_, lineId) = await ConfirmWithOneLineRemoved(removed);

        var outcome = await _service.TrackAsync(lineId);

        Assert.True(outcome.Retracked);
        await using var db = _db.CreateDbContext();
        var stored = await db.Products.SingleAsync(p => p.Id == eggs.Id);
        Assert.True(stored.IsTracked);
        Assert.Equal(5m, stored.QuantityOnHand);
    }

    // --- refusals write nothing ----------------------------------------------

    [Fact]
    public async Task Two_products_sharing_the_name_refuse_and_write_nothing()
    {
        await SeedProduct("Bread");
        await SeedProduct("bread");
        var (_, lineId) = await ConfirmWithOneLineRemoved(Line("Bread"));

        var outcome = await _service.TrackAsync(lineId);

        Assert.Equal(ReceiptLineProductService.Status.Ambiguous, outcome.Status);
        await AssertNothingWritten(lineId);
    }

    [Fact]
    public async Task A_name_with_no_identity_refuses_and_writes_nothing()
    {
        var (_, lineId) = await ConfirmWithOneLineRemoved(Line("!!"));

        var outcome = await _service.TrackAsync(lineId);

        Assert.Equal(ReceiptLineProductService.Status.Unnamable, outcome.Status);
        await AssertNothingWritten(lineId);
    }

    [Fact]
    public async Task A_pending_receipts_line_is_left_to_review()
    {
        int lineId;
        await using (var db = _db.CreateDbContext())
        {
            var line = Line("Dragonfruit Salsa");
            db.Receipts.Add(new Receipt { Merchant = "Walmart", ImagePath = "pending", Lines = [line] });
            await db.SaveChangesAsync();
            lineId = line.Id;
        }

        var outcome = await _service.TrackAsync(lineId);

        Assert.Equal(ReceiptLineProductService.Status.NotConfirmed, outcome.Status);
        await using var check = _db.CreateDbContext();
        Assert.Empty(await check.Products.ToListAsync());
        Assert.Empty(await check.PurchaseEvents.ToListAsync());
    }

    [Fact]
    public async Task A_line_already_linked_reports_its_product_and_records_no_second_purchase()
    {
        var (_, lineId) = await ConfirmWithOneLineRemoved(Line("Dragonfruit Salsa"));
        var first = await _service.TrackAsync(lineId);

        var second = await _service.TrackAsync(lineId); // a second tab, or a double tap

        Assert.Equal(ReceiptLineProductService.Status.AlreadyLinked, second.Status);
        Assert.Equal(first.ProductId, second.ProductId);
        Assert.Equal("Dragonfruit Salsa", second.ProductName);
        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.PurchaseEvents.CountAsync(p => p.ProductId == first.ProductId));
    }

    [Fact]
    public async Task Another_households_line_is_not_found_and_untouched()
    {
        var (_, lineId) = await ConfirmWithOneLineRemoved(Line("Dragonfruit Salsa"));

        _db.HouseholdId = "hh-other";
        var outcome = await _service.TrackAsync(lineId);

        Assert.Equal(ReceiptLineProductService.Status.NotFound, outcome.Status);
        _db.HouseholdId = "hh-test";
        await AssertNothingWritten(lineId);
    }

    [Fact]
    public async Task An_unknown_line_is_not_found()
    {
        Assert.Equal(ReceiptLineProductService.Status.NotFound, (await _service.TrackAsync(424242)).Status);
    }

    // --- the receipt's removal still unwinds everything ----------------------

    [Fact]
    public async Task Removing_the_receipt_takes_back_the_tracked_purchase_and_the_product_it_introduced()
    {
        var (receiptId, lineId) = await ConfirmWithOneLineRemoved(Line("Dragonfruit Salsa"));
        await _service.TrackAsync(lineId);

        var storage = new ReceiptStorage(new AppPaths(_dataDir, Path.Combine(_dataDir, "receipts")),
            new FakeCurrentHousehold(), NullLogger<ReceiptStorage>.Instance);
        var removal = await new ReceiptRemovalService(_db, storage, NullLogger<ReceiptRemovalService>.Instance)
            .RemoveAsync(receiptId);

        Assert.Equal(2, removal.Purchases);       // the confirm's milk AND the tracked salsa
        Assert.Equal(2, removal.ProductsRemoved); // both introduced by this receipt, no other history
        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Empty(await db.PurchaseEvents.ToListAsync());
    }

    // --- the plan the page labels its button with ----------------------------

    [Theory]
    [InlineData("Granola", ReceiptLineProductService.PlanKind.Create)]
    [InlineData("whole-milk", ReceiptLineProductService.PlanKind.Link)]
    [InlineData("Bread", ReceiptLineProductService.PlanKind.Ambiguous)]
    [InlineData("  ", ReceiptLineProductService.PlanKind.Unnamable)]
    [InlineData("--", ReceiptLineProductService.PlanKind.Unnamable)]
    public void The_plan_is_decided_by_identity(string name, ReceiptLineProductService.PlanKind expected)
    {
        Product[] catalog =
        [
            new() { Id = 1, Name = "Whole Milk" },
            new() { Id = 2, Name = "Bread" },
            new() { Id = 3, Name = "BREAD" },
        ];

        var plan = ReceiptLineProductService.PlanFor(name, catalog);

        Assert.Equal(expected, plan.Kind);
        Assert.Equal(expected == ReceiptLineProductService.PlanKind.Link ? 1 : null, plan.Existing?.Id);
    }

    private async Task AssertNothingWritten(int lineId)
    {
        await using var db = _db.CreateDbContext();
        Assert.Null((await db.ReceiptLines.SingleAsync(l => l.Id == lineId)).ProductId);
        // Only the kept line's purchase exists — the refused line recorded none.
        Assert.Equal(1, await db.PurchaseEvents.CountAsync());
    }
}
