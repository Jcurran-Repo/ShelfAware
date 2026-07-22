using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// "Remove this receipt" undoes everything its confirm did — and nothing anyone else did. The
/// headline scenario is the duplicate upload: Smart confirm commits a trusted dupe without a review
/// pause, so removal is the escape hatch that keeps one mis-click from permanently skewing cadences.
/// </summary>
public class ReceiptRemovalServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-web-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private ReceiptStorage Storage() => new(
        new AppPaths(_dataDir, Path.Combine(_dataDir, "receipts")),
        new FakeCurrentHousehold(),
        NullLogger<ReceiptStorage>.Instance);

    private ReceiptRemovalService Service() => new(_db, Storage(), NullLogger<ReceiptRemovalService>.Instance);

    private ReceiptConfirmationService Confirmer() => new(_db);

    private static readonly DateOnly Dated = new(2026, 7, 1);

    /// <summary>Persist a pending receipt the way an upload does, then confirm it through the ONE
    /// confirm path — so removal is tested against exactly what a real confirm produces.</summary>
    private async Task<int> ConfirmReceipt(
        bool writeAliases, params (string Raw, string Name, int ProductId)[] lines)
    {
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var receipt = new Receipt
            {
                Merchant = "Walmart",
                PurchasedAt = Dated,
                ImagePath = "removal-test",
                Lines = lines.Select(l => new ReceiptLine
                {
                    RawText = l.Raw, NormalizedName = l.Name, Quantity = 1, Confidence = 0.9m,
                }).ToList(),
            };
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync();
            id = receipt.Id;
        }
        await Confirmer().ConfirmAsync(id, Dated,
            lines.Select(l => new ReceiptConfirmationService.ConfirmLine(
                l.Raw, l.Name, null, null, null, 1, Category.Pantry, [], l.ProductId)).ToList(),
            writeAliases: writeAliases);
        return id;
    }

    [Fact]
    public async Task Removes_the_receipt_its_purchases_and_the_products_it_introduced()
    {
        var id = await ConfirmReceipt(writeAliases: true,
            ("GV WHL MLK", "Whole Milk", 0), ("DRAGON SALSA", "Dragonfruit Salsa", 0));

        var outcome = await Service().RemoveAsync(id);

        Assert.True(outcome.Found);
        Assert.False(outcome.Untraceable);
        Assert.Equal(2, outcome.Purchases);
        Assert.Equal(2, outcome.ProductsRemoved);
        Assert.Equal(0, outcome.AliasesRemoved); // they rode the product cascade, not the explicit path
        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Receipts.CountAsync());
        Assert.Equal(0, await db.ReceiptLines.CountAsync());
        Assert.Equal(0, await db.PurchaseEvents.CountAsync());
        Assert.Equal(0, await db.Products.CountAsync());
        Assert.Equal(0, await db.ProductAliases.CountAsync());
    }

    [Fact]
    public async Task The_duplicate_upload_scenario_removes_only_the_dupes_purchases()
    {
        // First (legitimate) confirm creates the product and teaches the alias…
        var first = await ConfirmReceipt(writeAliases: true, ("GV WHL MLK", "Whole Milk", 0));
        int productId;
        await using (var db = _db.CreateDbContext())
        {
            productId = (await db.Products.SingleAsync()).Id;
        }
        // …then the accidental re-upload records the same line against the existing product.
        var dupe = await ConfirmReceipt(writeAliases: false, ("GV WHL MLK", "Whole Milk", productId));

        var outcome = await Service().RemoveAsync(dupe);

        Assert.Equal(1, outcome.Purchases);
        Assert.Equal(0, outcome.ProductsRemoved); // the product belongs to the FIRST receipt's history
        await using var check = _db.CreateDbContext();
        Assert.NotNull(await check.Products.SingleOrDefaultAsync(p => p.Id == productId));
        var remaining = await check.PurchaseEvents.SingleAsync();
        Assert.Equal(first, remaining.ReceiptId);         // the real purchase survives
        Assert.Equal(1, await check.ProductAliases.CountAsync()); // the alias the human taught survives
        Assert.NotNull(await check.Receipts.SingleOrDefaultAsync(r => r.Id == first));
    }

    [Fact]
    public async Task A_product_that_gathered_other_history_is_kept_with_its_breadcrumb_cleared()
    {
        var id = await ConfirmReceipt(writeAliases: false, ("DRAGON SALSA", "Dragonfruit Salsa", 0));
        await using (var db = _db.CreateDbContext())
        {
            var product = await db.Products.SingleAsync();
            db.InventorySignals.Add(new InventorySignal
            {
                ProductId = product.Id, Kind = SignalKind.OutNow,
                SignaledAt = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero),
            });
            await db.SaveChangesAsync();
        }

        var outcome = await Service().RemoveAsync(id);

        Assert.Equal(0, outcome.ProductsRemoved);
        Assert.Equal(1, outcome.ProductsKept);
        await using var check = _db.CreateDbContext();
        var kept = await check.Products.SingleAsync();
        Assert.Null(kept.CreatedByReceiptId);   // the receipt is gone; no pointer at a ghost
        Assert.Equal(0, await check.PurchaseEvents.CountAsync()); // its purchases still go
    }

    [Fact]
    public async Task An_alias_retaught_to_a_different_product_since_is_kept()
    {
        var first = await ConfirmReceipt(writeAliases: true, ("GV WHL MLK", "Whole Milk", 0));
        int otherProductId;
        await using (var db = _db.CreateDbContext())
        {
            var other = new Product { Name = "Oat Milk" };
            db.Products.Add(other);
            await db.SaveChangesAsync();
            otherProductId = other.Id;
        }
        // A later human confirm re-points the pairing (last write wins) — through the REAL confirm
        // path, so it becomes the alias's new teacher.
        await ConfirmReceipt(writeAliases: true, ("GV WHL MLK", "Whole Milk", otherProductId));

        var outcome = await Service().RemoveAsync(first);

        Assert.Equal(0, outcome.AliasesRemoved);
        await using var check = _db.CreateDbContext();
        Assert.Equal(otherProductId, (await check.ProductAliases.SingleAsync()).ProductId);
    }

    [Fact]
    public async Task An_alias_taught_on_a_kept_product_is_removed_explicitly()
    {
        // The receipt teaches an alias for a product that PRE-dates it — the product stays, so the
        // alias can't ride any cascade and must be un-taught by the removal itself.
        int productId;
        await using (var db = _db.CreateDbContext())
        {
            var existing = new Product { Name = "Whole Milk" };
            db.Products.Add(existing);
            await db.SaveChangesAsync();
            productId = existing.Id;
        }
        var id = await ConfirmReceipt(writeAliases: true, ("GV WHL MLK", "Whole Milk", productId));

        var outcome = await Service().RemoveAsync(id);

        Assert.Equal(1, outcome.AliasesRemoved);
        Assert.Equal(0, outcome.ProductsRemoved); // pre-existing product is not the receipt's to take
        await using var check = _db.CreateDbContext();
        Assert.Equal(0, await check.ProductAliases.CountAsync());
        Assert.NotNull(await check.Products.SingleOrDefaultAsync(p => p.Id == productId));
    }

    [Fact]
    public async Task A_pre_provenance_confirm_is_refused_untouched()
    {
        var id = await ConfirmReceipt(writeAliases: false, ("GV WHL MLK", "Whole Milk", 0));
        await using (var db = _db.CreateDbContext())
        {
            // Age the data to the pre-provenance shape: purchases without a receipt link.
            await db.PurchaseEvents.ExecuteUpdateAsync(s => s.SetProperty(p => p.ReceiptId, (int?)null));
        }

        var outcome = await Service().RemoveAsync(id);

        Assert.True(outcome is { Found: true, Untraceable: true });
        await using var check = _db.CreateDbContext();
        Assert.Equal(1, await check.Receipts.CountAsync());       // nothing was deleted
        Assert.Equal(1, await check.PurchaseEvents.CountAsync());
    }

    [Fact]
    public async Task A_pending_receipt_removes_as_just_the_row_and_lines()
    {
        int id;
        await using (var db = _db.CreateDbContext())
        {
            var receipt = new Receipt
            {
                Merchant = "Walmart", PurchasedAt = Dated, ImagePath = "pending-test",
                Lines = [new ReceiptLine { RawText = "X", NormalizedName = "Widget", Quantity = 1 }],
            };
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync();
            id = receipt.Id;
        }

        var outcome = await Service().RemoveAsync(id);

        Assert.True(outcome is { Found: true, Untraceable: false, Purchases: 0 });
        await using var check = _db.CreateDbContext();
        Assert.Equal(0, await check.Receipts.CountAsync());
        Assert.Equal(0, await check.ReceiptLines.CountAsync());
    }

    [Fact]
    public async Task Another_households_receipt_is_invisible_and_untouched()
    {
        var id = await ConfirmReceipt(writeAliases: false, ("GV WHL MLK", "Whole Milk", 0));

        _db.HouseholdId = "hh-other";
        var outcome = await Service().RemoveAsync(id);
        _db.HouseholdId = "hh-test";

        Assert.False(outcome.Found); // the query filter never showed it to the other household
        await using var check = _db.CreateDbContext();
        Assert.Equal(1, await check.Receipts.CountAsync());
        Assert.Equal(1, await check.PurchaseEvents.CountAsync());
    }

    [Fact]
    public async Task Removing_twice_reports_not_found_the_second_time()
    {
        var id = await ConfirmReceipt(writeAliases: false, ("GV WHL MLK", "Whole Milk", 0));

        Assert.True((await Service().RemoveAsync(id)).Found);
        Assert.False((await Service().RemoveAsync(id)).Found);
    }

    [Fact]
    public async Task Purchases_recorded_by_chat_are_never_touched()
    {
        var id = await ConfirmReceipt(writeAliases: false, ("GV WHL MLK", "Whole Milk", 0));
        await using (var db = _db.CreateDbContext())
        {
            var product = await db.Products.SingleAsync();
            db.PurchaseEvents.Add(new PurchaseEvent
            {
                ProductId = product.Id, PurchasedAt = Dated.AddDays(1), Source = PurchaseSource.Chat,
            });
            await db.SaveChangesAsync();
        }

        await Service().RemoveAsync(id);

        await using var check = _db.CreateDbContext();
        var remaining = await check.PurchaseEvents.SingleAsync();
        Assert.Equal(PurchaseSource.Chat, remaining.Source); // no ReceiptId — not this receipt's to take
        // And the product it belongs to was KEPT (the chat purchase is "other history").
        Assert.Equal(1, await check.Products.CountAsync());
    }
}
