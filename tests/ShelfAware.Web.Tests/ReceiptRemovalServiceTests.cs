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

    /// <summary>Seeds a counted product with an established count, the way turning counting on and
    /// counting the shelf once would.</summary>
    private async Task<int> CountedProduct(string name, decimal onHand, decimal? countedDaysAgo = null)
    {
        await using var db = _db.CreateDbContext();
        var product = new Product
        {
            Name = name,
            Category = Category.Meat,
            TrackQuantity = true,
            QuantityOnHand = onHand,
            QuantityCountedAt = DateTimeOffset.Now.AddDays(-(double)(countedDaysAgo ?? 30)),
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<Product> ReadProduct(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Products.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task Confirm_then_remove_returns_the_count_to_where_it_started()
    {
        // §13.2's invariant, end to end through both real services: whatever a confirm adds, its undo
        // takes back. This is the one that would catch the two halves drifting apart.
        var id = await CountedProduct("Beef Chuck Roast", onHand: 5);
        var before = await ReadProduct(id);

        var receipt = await ConfirmReceipt(writeAliases: false, ("CHUCK ROAST", "Beef Chuck Roast", id));
        Assert.Equal(6m, (await ReadProduct(id)).QuantityOnHand); // the line's quantity is 1

        await Service().RemoveAsync(receipt);

        var after = await ReadProduct(id);
        Assert.Equal(before.QuantityOnHand, after.QuantityOnHand);
        // And the undo did NOT count as a human having looked — the attestation date is untouched, so
        // the staleness check still measures from the last real count.
        Assert.Equal(before.QuantityCountedAt, after.QuantityCountedAt);
    }

    [Fact]
    public async Task An_absolute_count_taken_after_the_confirm_is_not_overruled_by_removal()
    {
        // The duplicate-upload aftermath, in order: a dupe confirms (+1), the household recounts the
        // shelf (6 — ground truth, phantom excluded), the dupe is removed. Subtracting past that look
        // would overrule newer, better evidence. Skipping is sound ONLY because a relative move never
        // advances the attestation clock — the test below pins that half.
        var id = await CountedProduct("Beef Chuck Roast", onHand: 5);
        var receipt = await ConfirmReceipt(writeAliases: false, ("CHUCK ROAST", "Beef Chuck Roast", id));
        Assert.Equal(6m, (await ReadProduct(id)).QuantityOnHand);

        await using (var db = _db.CreateDbContext())
        {
            var p = await db.Products.SingleAsync(x => x.Id == id);
            StockLedger.Attest(p, 6, DateTimeOffset.Now.AddMinutes(1)); // the look, after the confirm
            await db.SaveChangesAsync();
        }

        await Service().RemoveAsync(receipt);

        Assert.Equal(6m, (await ReadProduct(id)).QuantityOnHand); // the look wins
    }

    [Fact]
    public async Task A_relative_move_after_the_confirm_does_not_shield_the_count_from_removal()
    {
        // "Used one" between the dupe's confirm and its removal carries the phantom stock forward — it
        // re-baselines nothing — so the removal must still subtract, or the count keeps stock that
        // never existed and the buy list goes quiet about it (the failure you find by running out).
        var id = await CountedProduct("Beef Chuck Roast", onHand: 5);
        var receipt = await ConfirmReceipt(writeAliases: false, ("CHUCK ROAST", "Beef Chuck Roast", id));

        await using (var db = _db.CreateDbContext())
        {
            var p = await db.Products.SingleAsync(x => x.Id == id);
            StockLedger.AdjustByHuman(p, -1, DateTimeOffset.Now.AddMinutes(1)); // 6 → 5, clock untouched
            await db.SaveChangesAsync();
        }

        await Service().RemoveAsync(receipt);

        Assert.Equal(4m, (await ReadProduct(id)).QuantityOnHand); // the dupe's +1 comes back off
    }

    [Fact]
    public async Task A_pre_timestamp_confirm_subtracts_exactly_as_it_always_did()
    {
        // Receipts confirmed before ConfirmedAt existed carry NULL — no moment to compare a count
        // against, so removal behaves as it did before the column: subtract, err toward early rebuy.
        var id = await CountedProduct("Beef Chuck Roast", onHand: 5);
        var receipt = await ConfirmReceipt(writeAliases: false, ("CHUCK ROAST", "Beef Chuck Roast", id));

        await using (var db = _db.CreateDbContext())
        {
            (await db.Receipts.SingleAsync(r => r.Id == receipt)).ConfirmedAt = null; // a pre-v4.1 confirm
            var p = await db.Products.SingleAsync(x => x.Id == id);
            StockLedger.Attest(p, 6, DateTimeOffset.Now.AddMinutes(1)); // even with a newer look…
            await db.SaveChangesAsync();
        }

        await Service().RemoveAsync(receipt);

        Assert.Equal(5m, (await ReadProduct(id)).QuantityOnHand); // …the subtract still runs
    }

    [Fact]
    public async Task A_confirm_leaves_an_uncounted_product_alone()
    {
        // Opt-in stays opt-in: confirming a receipt must not start counting an item, nor invent a total
        // for one that's counted but never counted.
        await using (var db = _db.CreateDbContext())
        {
            db.Products.Add(new Product { Name = "Bananas", Category = Category.Produce });
            db.Products.Add(new Product { Name = "Rice", Category = Category.Pantry, TrackQuantity = true });
            await db.SaveChangesAsync();
        }
        int bananas, rice;
        await using (var db = _db.CreateDbContext())
        {
            bananas = (await db.Products.SingleAsync(p => p.Name == "Bananas")).Id;
            rice = (await db.Products.SingleAsync(p => p.Name == "Rice")).Id;
        }

        await ConfirmReceipt(writeAliases: false,
            ("BANANAS", "Bananas", bananas), ("RICE", "Rice", rice));

        Assert.Null((await ReadProduct(bananas)).QuantityOnHand); // never opted in
        Assert.Null((await ReadProduct(rice)).QuantityOnHand);    // opted in, no baseline counted yet
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
    public async Task A_counted_product_is_kept_when_its_introducing_receipt_is_removed()
    {
        // The census aftermath (§13.8): a receipt introduces a product, a shelf census attests a
        // count — which writes NO purchase and, for a positive count, NO signal — and the receipt
        // is later removed. The attestation is a human act on the product, so it is history:
        // deleting the product here destroyed the count with it, while a stray RunningLow tap
        // would have saved what a deliberate count could not.
        var receipt = await ConfirmReceipt(writeAliases: false, ("FRZ PEAS", "Frozen Peas", 0));
        int productId;
        await using (var db = _db.CreateDbContext())
        {
            productId = (await db.Products.SingleAsync()).Id;
        }
        // The census's own write path — the real service, so the probe can't be kinder than the app.
        var census = await new CensusConfirmationService(_db).ConfirmAsync(
            [new CensusConfirmationService.CensusRow("Frozen Peas", Category.Frozen, 12m, productId)]);
        Assert.Equal(1, census.Counted);

        var outcome = await Service().RemoveAsync(receipt);

        Assert.Equal(0, outcome.ProductsRemoved);
        Assert.Equal(1, outcome.ProductsKept);
        await using var check = _db.CreateDbContext();
        var kept = await check.Products.SingleAsync(p => p.Id == productId);
        Assert.Null(kept.CreatedByReceiptId); // breadcrumb cleared, like any kept product
        // Exactly 12: counted after the confirm, so the subtract guard also stands down — 11 here
        // means the delete was fixed but the subtract ran anyway.
        Assert.Equal(12m, kept.QuantityOnHand);
        Assert.Equal(0, await check.PurchaseEvents.CountAsync()); // the purchase itself still goes
    }

    [Fact]
    public async Task A_pre_timestamp_confirm_still_cannot_corrupt_an_introduced_products_count()
    {
        // The null-ConfirmedAt rule ("no moment to compare — subtract as always") is right for a
        // product that PRE-dated the receipt and wrong for one the receipt INTRODUCED: it did not
        // exist before its own confirm, so every attestation on it provably postdates that confirm
        // even with the timestamp missing. Without the introduced-arm, keeping the product while
        // subtracting silently corrupted the very count the keep exists to preserve — attested 12
        // read back as 11. The pre-existing-product sibling above pins the other arm: there the
        // order really is unknowable, and the subtract still errs toward an early rebuy.
        var receipt = await ConfirmReceipt(writeAliases: false, ("FRZ PEAS", "Frozen Peas", 0));
        await using (var db = _db.CreateDbContext())
        {
            (await db.Receipts.SingleAsync(r => r.Id == receipt)).ConfirmedAt = null; // pre-v4.1
            var p = await db.Products.SingleAsync();
            StockLedger.Attest(p, 12m, DateTimeOffset.Now.AddMinutes(1));
            await db.SaveChangesAsync();
        }

        var outcome = await Service().RemoveAsync(receipt);

        Assert.Equal(1, outcome.ProductsKept);
        await using var check = _db.CreateDbContext();
        Assert.Equal(12m, (await check.Products.SingleAsync()).QuantityOnHand); // kept AND whole
    }

    [Fact]
    public async Task A_dormant_count_is_history_too()
    {
        // Stop-counting keeps the number and its date as a historical fact the app promises to show
        // (§13.1), so a dormant attestation must keep the product exactly as a live one does. This
        // is what pins the check to the attestation DATE: keying it on TrackQuantity instead would
        // delete precisely the kept history dormancy exists to preserve.
        var receipt = await ConfirmReceipt(writeAliases: false, ("FRZ PEAS", "Frozen Peas", 0));
        await using (var db = _db.CreateDbContext())
        {
            var p = await db.Products.SingleAsync();
            StockLedger.Attest(p, 12m, DateTimeOffset.Now.AddMinutes(1));
            StockLedger.StopCounting(p);
            await db.SaveChangesAsync();
        }

        var outcome = await Service().RemoveAsync(receipt);

        Assert.Equal(1, outcome.ProductsKept);
        await using var check = _db.CreateDbContext();
        var kept = await check.Products.SingleAsync();
        Assert.False(kept.TrackQuantity);
        Assert.Equal(12m, kept.QuantityOnHand); // the frozen pair survives untouched
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
