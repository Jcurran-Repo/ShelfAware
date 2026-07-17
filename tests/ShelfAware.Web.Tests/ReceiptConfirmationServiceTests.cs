using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

public class ReceiptConfirmationServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ReceiptConfirmationService _service;

    public ReceiptConfirmationServiceTests() => _service = new ReceiptConfirmationService(_db);

    public void Dispose() => _db.Dispose();

    // --- helpers -------------------------------------------------------------

    private async Task<Product> SeedProduct(string name, params string[] tags)
    {
        await using var db = _db.CreateDbContext();
        var product = new Product { Name = name, Tags = tags.Select(t => new ProductTag { Value = t }).ToList() };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private async Task<Receipt> SeedPending(string? merchant, params ReceiptLine[] lines)
    {
        await using var db = _db.CreateDbContext();
        var receipt = new Receipt { Merchant = merchant, ImagePath = "receipts/test", Lines = [.. lines] };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static ReceiptLine L(string raw, string name) => new() { RawText = raw, NormalizedName = name };

    private static ReceiptConfirmationService.ConfirmLine C(
        string raw, string name, int productId = 0, decimal qty = 1, string? brand = null,
        string? size = null, string? variety = null, string[]? tags = null, Category category = Category.Other) =>
        new(raw, name, brand, size, variety, qty, category, tags ?? [], productId);

    // --- the happy path ------------------------------------------------------

    [Fact]
    public async Task Confirms_lines_into_purchases_products_lines_and_aliases()
    {
        var milk = await SeedProduct("Whole Milk");
        var receipt = await SeedPending("Walmart",
            L("GV WHL MLK", "Whole Milk"), L("DRAGON SALSA", "Dragonfruit Salsa"));

        var outcome = await _service.ConfirmAsync(receipt.Id, new DateOnly(2026, 7, 1),
            [
                C("GV WHL MLK", "Whole Milk", milk.Id, qty: 2, brand: "Great Value", size: "1 gal"),
                C("DRAGON SALSA", "Dragonfruit Salsa", category: Category.Pantry),
            ],
            writeAliases: true);

        Assert.False(outcome.AlreadyConfirmed);
        Assert.Equal(2, outcome.Purchases);
        Assert.Equal(1, outcome.NewProducts);

        await using var db = _db.CreateDbContext();
        Assert.Equal(ReceiptStatus.Confirmed, (await db.Receipts.SingleAsync()).Status);

        var purchases = await db.PurchaseEvents.ToListAsync();
        Assert.Equal(2, purchases.Count);
        Assert.All(purchases, p => Assert.Equal(new DateOnly(2026, 7, 1), p.PurchasedAt));
        var milkBuy = purchases.Single(p => p.ProductId == milk.Id);
        Assert.Equal(2m, milkBuy.Quantity);
        Assert.Equal("Great Value", milkBuy.Brand);
        Assert.Equal("1 gal", milkBuy.Size);

        var salsa = await db.Products.SingleAsync(p => p.Name == "Dragonfruit Salsa");
        Assert.Equal(Category.Pantry, salsa.Category);

        Assert.Equal(2, await db.ProductAliases.CountAsync(a => a.Merchant == "Walmart"));
        Assert.All(await db.ReceiptLines.ToListAsync(), l => Assert.NotNull(l.ProductId));
    }

    // --- variety mirrors brand + size onto both the purchase and the stored line ---

    [Fact]
    public async Task Copies_variety_onto_the_purchase_and_the_stored_line()
    {
        var receipt = await SeedPending("Walmart", L("KOOL AID STRAW DRK MIX", "Drink Mix"));

        await _service.ConfirmAsync(receipt.Id, new DateOnly(2026, 7, 1),
            [C("KOOL AID STRAW DRK MIX", "Drink Mix", brand: "Kool-Aid", variety: "  Strawberry  ")],
            writeAliases: false);

        await using var db = _db.CreateDbContext();
        Assert.Equal("Strawberry", (await db.PurchaseEvents.SingleAsync()).Variety); // trimmed
        Assert.Equal("Strawberry", (await db.ReceiptLines.SingleAsync()).Variety);
    }

    // --- idempotency (the double-click bug) ----------------------------------

    [Fact]
    public async Task Confirming_twice_records_nothing_the_second_time()
    {
        var receipt = await SeedPending("Walmart", L("RIBS", "Pork Ribs"));
        ReceiptConfirmationService.ConfirmLine[] lines = [C("RIBS", "Pork Ribs")];

        var first = await _service.ConfirmAsync(receipt.Id, new DateOnly(2026, 7, 1), lines, writeAliases: true);
        var second = await _service.ConfirmAsync(receipt.Id, new DateOnly(2026, 7, 1), lines, writeAliases: true);

        Assert.False(first.AlreadyConfirmed);
        Assert.True(second.AlreadyConfirmed);
        Assert.Equal(0, second.Purchases);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.PurchaseEvents.CountAsync());
        Assert.Equal(1, await db.Products.CountAsync());
    }

    // --- retracking: a purchase ends "don't want it for a while" ---------------

    [Fact]
    public async Task Confirming_a_purchase_retracks_an_ignored_product()
    {
        // The grocery list's "Ignore for now" untracks a product; buying it again is the signal
        // to resume predictions — on any confirm path, without the user having to remember.
        int cocoaId;
        await using (var db = _db.CreateDbContext())
        {
            var cocoa = new Product { Name = "Cocoa Powder", IsTracked = false };
            db.Products.Add(cocoa);
            await db.SaveChangesAsync();
            cocoaId = cocoa.Id;
        }
        var milk = await SeedProduct("Whole Milk"); // stays tracked — must not count as retracked
        var receipt = await SeedPending("Walmart", L("COCOA PWDR", "Cocoa Powder"), L("GV WHL MLK", "Whole Milk"));

        var outcome = await _service.ConfirmAsync(receipt.Id, new DateOnly(2026, 7, 1),
            [C("COCOA PWDR", "Cocoa Powder", cocoaId), C("GV WHL MLK", "Whole Milk", milk.Id)],
            writeAliases: false);

        Assert.Equal(1, outcome.Retracked);
        await using var read = _db.CreateDbContext();
        Assert.True((await read.Products.SingleAsync(p => p.Id == cocoaId)).IsTracked);
    }

    // --- eval ground-truth trust boundary ---------------------------------------

    [Fact]
    public async Task Verified_for_eval_is_set_only_when_the_reviewer_asserted_it()
    {
        // The default (machine confirms, unticked reviews) leaves the flag off — an unreviewed
        // receipt must never become accuracy ground truth.
        var quiet = await SeedPending("Walmart", L("GV WHL MLK", "Whole Milk"));
        await _service.ConfirmAsync(quiet.Id, new DateOnly(2026, 7, 1),
            [C("GV WHL MLK", "Whole Milk")], writeAliases: false);

        var asserted = await SeedPending("Walmart", L("RIBS", "Pork Ribs"));
        await _service.ConfirmAsync(asserted.Id, new DateOnly(2026, 7, 1),
            [C("RIBS", "Pork Ribs")], writeAliases: true, verifiedForEval: true);

        await using var db = _db.CreateDbContext();
        Assert.False((await db.Receipts.SingleAsync(r => r.Id == quiet.Id)).VerifiedForEval);
        Assert.True((await db.Receipts.SingleAsync(r => r.Id == asserted.Id)).VerifiedForEval);
    }

    // --- alias trust boundary -------------------------------------------------

    [Fact]
    public async Task Machine_confirmed_receipts_write_no_aliases()
    {
        var milk = await SeedProduct("Whole Milk");
        var receipt = await SeedPending("Walmart", L("GV WHL MLK", "Whole Milk"));

        await _service.ConfirmAsync(receipt.Id, new DateOnly(2026, 7, 1),
            [C("GV WHL MLK", "Whole Milk", milk.Id)], writeAliases: false);

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.ProductAliases.CountAsync());
        Assert.Equal(1, await db.PurchaseEvents.CountAsync()); // purchases still recorded
    }

    // --- input hardening -------------------------------------------------------

    [Fact]
    public async Task Nonpositive_quantity_clamps_to_one_and_future_date_clamps_to_today()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var receipt = await SeedPending("Walmart", L("RIBS", "Pork Ribs"));

        await _service.ConfirmAsync(receipt.Id, today.AddDays(30),
            [C("RIBS", "Pork Ribs", qty: 0)], writeAliases: true);

        await using var db = _db.CreateDbContext();
        var purchase = await db.PurchaseEvents.SingleAsync();
        Assert.Equal(1m, purchase.Quantity);
        Assert.Equal(today, purchase.PurchasedAt);
        Assert.Equal(today, (await db.Receipts.SingleAsync()).PurchasedAt);
    }

    // --- tag canonicalization (the drift the auto path had) ---------------------

    [Fact]
    public async Task Tags_canonicalize_against_the_global_vocabulary()
    {
        await SeedProduct("Hot Sauce", "Spicy"); // "Spicy" exists on ANOTHER product
        var receipt = await SeedPending("Walmart", L("SALSA", "Salsa Verde"));

        // "spicy " → existing global "Spicy"; "Snacks" → near-duplicate of the seed tag "Snack".
        await _service.ConfirmAsync(receipt.Id, new DateOnly(2026, 7, 1),
            [C("SALSA", "Salsa Verde", tags: ["spicy ", "Snacks"])], writeAliases: true);

        await using var db = _db.CreateDbContext();
        var salsa = await db.Products.Include(p => p.Tags).SingleAsync(p => p.Name == "Salsa Verde");
        Assert.Equal(["Snack", "Spicy"], salsa.Tags.Select(t => t.Value).OrderBy(v => v).ToArray());
    }

    // --- duplicate lines --------------------------------------------------------

    [Fact]
    public async Task Duplicate_raw_lines_share_one_product_and_one_alias_but_link_distinct_lines()
    {
        var receipt = await SeedPending("Walmart", L("RIBS", "Pork Ribs"), L("RIBS", "Pork Ribs"));

        var outcome = await _service.ConfirmAsync(receipt.Id, new DateOnly(2026, 7, 1),
            [C("RIBS", "Pork Ribs"), C("RIBS", "Pork Ribs")], writeAliases: true);

        Assert.Equal(2, outcome.Purchases);
        Assert.Equal(1, outcome.NewProducts);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Products.CountAsync());
        Assert.Equal(2, await db.PurchaseEvents.CountAsync());
        Assert.Equal(1, await db.ProductAliases.CountAsync()); // unique (Merchant, RawText) index holds
        Assert.All(await db.ReceiptLines.ToListAsync(), l => Assert.NotNull(l.ProductId));
    }
}
