using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;
using ShelfAware.Web.Undo;

namespace ShelfAware.Web.Tests;

/// <summary>The activity-log backbone and the first undo handler (PurchaseAdded), exercised end-to-end on
/// real SQLite: recording through <see cref="EfPantryStore"/>, and precondition-checked reversal through
/// <see cref="ActivityLogService"/>. The refusals are the point — a blind undo is this repo's signature
/// two-places-disagree failure across time — so both directions of every precondition are pinned.</summary>
public sealed class ActivityLogTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ActivityLogService _log;
    private readonly EfPantryStore _store;

    public ActivityLogTests()
    {
        _log = UndoTesting.Log(_db);
        _store = new EfPantryStore(_db, _log);
    }

    public void Dispose() => _db.Dispose();

    // ---- recording ----

    [Fact]
    public async Task Buying_records_an_undoable_activity_entry()
    {
        var id = await SeedProduct();
        await _store.AddPurchaseAsync(id, new DateOnly(2026, 8, 17), 2, PurchaseSource.Manual);

        var entry = await LatestEntry();
        Assert.Equal(ActivityKind.PurchaseAdded, entry.Kind);
        Assert.Equal(Reversibility.Reversible, entry.Reversibility);
        Assert.Equal("Bought 2 × Whole Milk", entry.Summary);
        Assert.Equal("Manual", entry.Source);
        Assert.Null(entry.UndoneAt);
        Assert.Equal(1, await PurchaseCount(id));
    }

    // ---- undo: the happy path and every refusal ----

    [Fact]
    public async Task Undo_deletes_the_purchase_and_takes_the_count_back()
    {
        var id = await SeedProduct(count: 5, countedAt: DateTimeOffset.Now.AddDays(-3));
        await _store.AddPurchaseAsync(id, new DateOnly(2026, 8, 17), 1, PurchaseSource.Manual);
        Assert.Equal(6m, (await ReadProduct(id))!.QuantityOnHand); // the buy moved the count up

        var entry = await LatestEntry();
        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));

        Assert.Equal(0, await PurchaseCount(id));                   // purchase gone
        Assert.Equal(5m, (await ReadProduct(id))!.QuantityOnHand);  // count back where it was
        Assert.NotNull((await Entry(entry.Id))!.UndoneAt);          // stamped
    }

    [Fact]
    public async Task Undo_refuses_when_the_purchase_quantity_was_edited_since()
    {
        var id = await SeedProduct(count: 5, countedAt: DateTimeOffset.Now.AddDays(-3));
        await _store.AddPurchaseAsync(id, new DateOnly(2026, 8, 17), 1, PurchaseSource.Manual);
        var entry = await LatestEntry(); // the PurchaseAdded entry, BEFORE the edit records its own
        var purchaseId = await OnlyPurchaseId(id);
        await _store.SetPurchaseQuantityAsync(purchaseId, 3); // a later action changed the same row

        Assert.Equal(UndoOutcome.Superseded, await _log.UndoAsync(entry.Id));

        Assert.Equal(1, await PurchaseCount(id));            // still there
        Assert.Null((await Entry(entry.Id))!.UndoneAt);      // not marked undone
    }

    [Fact]
    public async Task Undo_leaves_a_count_re_attested_after_the_buy_alone()
    {
        var id = await SeedProduct(count: 5, countedAt: DateTimeOffset.Now.AddDays(-3));
        await _store.AddPurchaseAsync(id, new DateOnly(2026, 8, 17), 1, PurchaseSource.Manual);
        var entry = await LatestEntry();

        // A human looks at the shelf AFTER the buy and states 10 — that attestation is the truth now, so
        // the undo must delete the purchase but NOT subtract from the re-counted number.
        await ReAttestCount(id, 10m, at: entry.OccurredAt.AddMinutes(1));

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.Equal(0, await PurchaseCount(id));                   // purchase still removed
        Assert.Equal(10m, (await ReadProduct(id))!.QuantityOnHand); // count left at the attested value
    }

    [Fact]
    public async Task Undoing_twice_reports_AlreadyUndone_the_second_time()
    {
        var id = await SeedProduct();
        await _store.AddPurchaseAsync(id, new DateOnly(2026, 8, 17), 1);
        var entry = await LatestEntry();

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.Equal(UndoOutcome.AlreadyUndone, await _log.UndoAsync(entry.Id));
    }

    [Fact]
    public async Task Undo_of_an_entry_whose_purchase_is_already_gone_reports_Gone()
    {
        var id = await SeedProduct();
        await _store.AddPurchaseAsync(id, new DateOnly(2026, 8, 17), 1);
        var entry = await LatestEntry();
        await DeleteAllPurchases(id); // another path removed it (a product-delete cascade, a receipt removal…)

        Assert.Equal(UndoOutcome.Gone, await _log.UndoAsync(entry.Id));
        Assert.Null((await Entry(entry.Id))!.UndoneAt);
    }

    [Fact]
    public async Task Undo_of_a_missing_entry_reports_Gone() =>
        Assert.Equal(UndoOutcome.Gone, await _log.UndoAsync(9999));

    [Fact]
    public async Task Peek_reports_undoability_without_undoing()
    {
        var id = await SeedProduct();
        await _store.AddPurchaseAsync(id, new DateOnly(2026, 8, 17), 1);
        var entry = await LatestEntry();

        Assert.Equal(UndoOutcome.Done, await _log.PeekAsync(entry.Id)); // it's undoable...
        Assert.Equal(1, await PurchaseCount(id));                       // ...but peeking didn't touch the purchase
        Assert.Null((await Entry(entry.Id))!.UndoneAt);                 // and didn't stamp it

        await DeleteAllPurchases(id);
        Assert.Equal(UndoOutcome.Gone, await _log.PeekAsync(entry.Id)); // now the reversal is a no-op → not undoable
    }

    // ---- service backbone ----

    [Fact]
    public async Task A_history_only_entry_cannot_be_undone()
    {
        int entryId;
        await using (var db = _db.CreateDbContext())
        {
            var e = new ActivityEntry
            {
                Kind = ActivityKind.ProductsMerged, OccurredAt = DateTimeOffset.Now,
                Summary = "Merged A into B", PayloadJson = "{}", Reversibility = Reversibility.NotReversible,
            };
            db.ActivityEntries.Add(e);
            await db.SaveChangesAsync();
            entryId = e.Id;
        }

        Assert.Equal(UndoOutcome.NotReversible, await _log.UndoAsync(entryId));
        Assert.Null((await Entry(entryId))!.UndoneAt);
    }

    [Fact]
    public async Task Recording_a_kind_with_no_handler_fails_fast()
    {
        var empty = new ActivityLogService(_db, [], Options.Create(new ActivityLogOptions()),
            NullLogger<ActivityLogService>.Instance);
        await using var db = _db.CreateDbContext();
        Assert.Throws<InvalidOperationException>(() => empty.Record(
            db, ActivityKind.PurchaseAdded, new PurchaseAddedPayload(1, 1, "X", new DateOnly(2026, 8, 17))));
    }

    [Fact]
    public async Task A_failed_record_rolls_back_the_whole_action()
    {
        // Atomicity: recording is inside the buy's transaction, so a record that throws (here, a recorder
        // with no handler) takes the purchase down with it — never an action without its undo entry.
        var brokenLog = new ActivityLogService(_db, [], Options.Create(new ActivityLogOptions()),
            NullLogger<ActivityLogService>.Instance);
        var store = new EfPantryStore(_db, brokenLog);
        var id = await SeedProduct();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AddPurchaseAsync(id, new DateOnly(2026, 8, 17), 1, PurchaseSource.Manual));

        Assert.Equal(0, await PurchaseCount(id)); // the buy rolled back with the failed record
    }

    [Fact]
    public async Task GetHistoryAsync_returns_newest_first()
    {
        var id = await SeedProduct();
        await _store.AddPurchaseAsync(id, new DateOnly(2026, 8, 1), 1);
        await _store.AddPurchaseAsync(id, new DateOnly(2026, 8, 2), 2);

        var history = await _log.GetHistoryAsync();
        Assert.Equal(2, history.Count);
        Assert.True(history[0].Id > history[1].Id); // newest first
    }

    [Fact]
    public async Task A_bounded_log_trims_the_oldest_rows_first()
    {
        var log = UndoTesting.Log(_db, maxRows: 3);
        var store = new EfPantryStore(_db, log);
        var id = await SeedProduct(name: "Milk");
        // Five buys (distinct quantities so the survivors are identifiable), trimmed to the newest 3.
        for (var i = 1; i <= 5; i++)
            await store.AddPurchaseAsync(id, new DateOnly(2026, 8, 17), i, PurchaseSource.Manual);

        var remaining = await log.GetHistoryAsync();
        Assert.Equal(
            new[] { "Bought 5 × Milk", "Bought 4 × Milk", "Bought 3 × Milk" },
            remaining.Select(e => e.Summary).ToArray());
    }

    // ---- isolation ----

    [Fact]
    public async Task A_household_cannot_undo_another_households_entry()
    {
        _db.HouseholdId = "hh-A";
        var id = await SeedProduct();
        await _store.AddPurchaseAsync(id, new DateOnly(2026, 8, 17), 1);
        var entry = await LatestEntry();

        _db.HouseholdId = "hh-B";
        Assert.Equal(UndoOutcome.Gone, await _log.UndoAsync(entry.Id)); // B can't even see A's entry

        _db.HouseholdId = "hh-A";
        Assert.Null((await Entry(entry.Id))!.UndoneAt); // A's entry untouched
        Assert.Equal(1, await PurchaseCount(id));       // A's purchase untouched
    }

    // ---- SignalRecorded ----

    [Fact]
    public async Task Recording_a_signal_logs_an_undoable_entry()
    {
        var id = await SeedProduct();
        await _store.RecordSignalAsync(id, SignalKind.Restocked);

        var entry = await LatestEntry();
        Assert.Equal(ActivityKind.SignalRecorded, entry.Kind);
        Assert.Equal("Restocked Whole Milk", entry.Summary);
        Assert.Equal(1, await SignalCount(id));
    }

    [Fact]
    public async Task Undoing_a_signal_deletes_the_row()
    {
        var id = await SeedProduct();
        await _store.RecordSignalAsync(id, SignalKind.OutNow);
        var entry = await LatestEntry();

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.Equal(0, await SignalCount(id));
        Assert.NotNull((await Entry(entry.Id))!.UndoneAt);
    }

    [Fact]
    public async Task Undoing_a_signal_whose_row_is_gone_reports_Gone()
    {
        var id = await SeedProduct();
        await _store.RecordSignalAsync(id, SignalKind.RunningLow);
        var entry = await LatestEntry();
        await DeleteAllSignals(id); // cleared by another path — undoing would be a no-op

        Assert.Equal(UndoOutcome.Gone, await _log.UndoAsync(entry.Id));
        Assert.Null((await Entry(entry.Id))!.UndoneAt);
    }

    // ---- PurchaseQuantityEdited ----

    [Fact]
    public async Task Undoing_a_quantity_edit_restores_the_old_quantity_and_count()
    {
        var id = await SeedProduct(count: 10, countedAt: DateTimeOffset.Now.AddDays(-3));
        var purchaseId = await SeedPurchase(id, qty: 2);
        await _store.SetPurchaseQuantityAsync(purchaseId, 5); // +3 to the count (10 → 13)
        Assert.Equal(13m, (await ReadProduct(id))!.QuantityOnHand);

        var entry = await LatestEntry();
        Assert.Equal("Changed Whole Milk quantity to 5", entry.Summary);
        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));

        Assert.Equal(2m, (await OnlyPurchase(id)).Quantity);       // old quantity back
        Assert.Equal(10m, (await ReadProduct(id))!.QuantityOnHand); // count back
    }

    [Fact]
    public async Task Undoing_a_quantity_edit_refuses_after_a_later_edit()
    {
        var id = await SeedProduct();
        var purchaseId = await SeedPurchase(id, qty: 2);
        await _store.SetPurchaseQuantityAsync(purchaseId, 5);
        var entry = await LatestEntry();
        await _store.SetPurchaseQuantityAsync(purchaseId, 8); // changed again since

        Assert.Equal(UndoOutcome.Superseded, await _log.UndoAsync(entry.Id));
        Assert.Equal(8m, (await OnlyPurchase(id)).Quantity); // untouched
    }

    // ---- PurchaseBrandEdited ----

    [Fact]
    public async Task Undoing_a_brand_edit_restores_the_old_brand()
    {
        var id = await SeedProduct();
        var purchaseId = await SeedPurchase(id, brand: "Store Brand");
        await _store.SetPurchaseBrandAsync(purchaseId, "Great Value");
        var entry = await LatestEntry();
        Assert.Equal("Set Whole Milk's brand to Great Value", entry.Summary);

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.Equal("Store Brand", (await OnlyPurchase(id)).Brand);
    }

    [Fact]
    public async Task Undoing_a_brand_edit_refuses_after_a_later_change()
    {
        var id = await SeedProduct();
        var purchaseId = await SeedPurchase(id, brand: "Store Brand");
        await _store.SetPurchaseBrandAsync(purchaseId, "Great Value");
        var entry = await LatestEntry();
        await _store.SetPurchaseBrandAsync(purchaseId, "Kirkland"); // changed again since

        Assert.Equal(UndoOutcome.Superseded, await _log.UndoAsync(entry.Id));
        Assert.Equal("Kirkland", (await OnlyPurchase(id)).Brand);
    }

    // ---- DefaultUnitSet ----

    [Fact]
    public async Task Undoing_a_unit_change_restores_the_old_unit()
    {
        var id = await SeedProduct();
        await _store.SetDefaultUnitAsync(id, "lb"); // from null
        var entry = await LatestEntry();
        Assert.Equal("Set Whole Milk's unit to lb", entry.Summary);

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.Null((await ReadProduct(id))!.DefaultUnit);
    }

    // ---- TrackingChanged ----

    [Fact]
    public async Task Undoing_a_tracking_change_flips_it_back()
    {
        var id = await SeedProduct(tracked: true);
        await _store.SetTrackingAsync(id, false); // stop tracking
        var entry = await LatestEntry();
        Assert.Equal("Stopped tracking Whole Milk", entry.Summary);

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.True((await ReadProduct(id))!.IsTracked);
    }

    [Fact]
    public async Task Setting_tracking_to_the_same_value_records_nothing()
    {
        var id = await SeedProduct(tracked: true);
        await _store.SetTrackingAsync(id, true); // no change — nothing to undo

        Assert.Empty(await _log.GetHistoryAsync());
    }

    // ---- CountSet ----

    [Fact]
    public async Task Setting_a_count_logs_an_undoable_entry_and_undo_restores_it()
    {
        var id = await SeedProduct(); // never counted
        await _store.SetQuantityAsync(id, 7); // absolute attest → count 7, opts into counting
        var p = (await ReadProduct(id))!;
        Assert.Equal(7m, p.QuantityOnHand);
        Assert.True(p.TrackQuantity);

        var entry = await LatestEntry();
        Assert.Equal("Set Whole Milk count to 7", entry.Summary);
        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));

        var after = (await ReadProduct(id))!;
        Assert.Null(after.QuantityOnHand); // back to unknown
        Assert.False(after.TrackQuantity); // and un-opted-in
    }

    [Fact]
    public async Task Undoing_a_count_that_asserted_zero_deletes_the_out_now_it_filed()
    {
        var id = await SeedProduct(count: 5, countedAt: DateTimeOffset.Now.AddDays(-2));
        await _store.SetQuantityAsync(id, 0); // asserted zero → files an OutNow
        Assert.Equal(1, await SignalCount(id));

        var entry = await LatestEntry();
        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.Equal(5m, (await ReadProduct(id))!.QuantityOnHand); // count restored
        Assert.Equal(0, await SignalCount(id));                    // and the OutNow removed
    }

    [Fact]
    public async Task Undoing_a_count_refuses_after_a_recount()
    {
        var id = await SeedProduct();
        await _store.SetQuantityAsync(id, 7);
        var entry = await LatestEntry();
        await _store.SetQuantityAsync(id, 12); // recounted since

        Assert.Equal(UndoOutcome.Superseded, await _log.UndoAsync(entry.Id));
        Assert.Equal(12m, (await ReadProduct(id))!.QuantityOnHand); // untouched
    }

    [Fact]
    public async Task Undoing_stop_counting_resumes_it()
    {
        var id = await SeedProduct(count: 5, countedAt: DateTimeOffset.Now.AddDays(-2));
        await _store.SetQuantityAsync(id, 0, stopCounting: true); // TrackQuantity → false
        Assert.False((await ReadProduct(id))!.TrackQuantity);

        var entry = await LatestEntry();
        Assert.Equal("Stopped counting Whole Milk", entry.Summary);
        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.True((await ReadProduct(id))!.TrackQuantity);
    }

    // ---- ProductCreated ----

    [Fact]
    public async Task Creating_a_product_logs_an_undoable_entry_and_undo_deletes_it()
    {
        var id = await _store.CreateProductAsync("Olive Oil", Category.Pantry, []);
        var entry = await LatestEntry();
        Assert.Equal("Added Olive Oil", entry.Summary);

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.False(await ProductExists(id));
    }

    [Fact]
    public async Task Undoing_a_product_create_refuses_if_it_gained_history()
    {
        var id = await _store.CreateProductAsync("Olive Oil", Category.Pantry, []);
        var entry = await LatestEntry();
        await SeedPurchase(id); // it earned its keep

        Assert.Equal(UndoOutcome.Superseded, await _log.UndoAsync(entry.Id));
        Assert.True(await ProductExists(id));
    }

    // ---- ExpirationSet ----

    [Fact]
    public async Task Undoing_an_expiration_change_restores_the_old_date()
    {
        var id = await SeedProduct();
        await SeedPurchase(id, qty: 1);
        await _store.SetExpirationAsync(id, new DateOnly(2026, 9, 1));
        var entry = await LatestEntry();
        Assert.Equal("Set Whole Milk expiration to Sep 1, 2026", entry.Summary);

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.Null((await OnlyPurchase(id)).ExpirationDate); // back to no date
    }

    [Fact]
    public async Task Undoing_an_expiration_change_refuses_after_a_later_change()
    {
        var id = await SeedProduct();
        await SeedPurchase(id, qty: 1);
        await _store.SetExpirationAsync(id, new DateOnly(2026, 9, 1));
        var entry = await LatestEntry();
        await _store.SetExpirationAsync(id, new DateOnly(2026, 10, 1)); // re-dated since

        Assert.Equal(UndoOutcome.Superseded, await _log.UndoAsync(entry.Id));
        Assert.Equal(new DateOnly(2026, 10, 1), (await OnlyPurchase(id)).ExpirationDate);
    }

    // ---- TagsAdded / SubstitutesAdded / GroceryExtrasAdded ----

    [Fact]
    public async Task Undoing_a_tag_add_removes_the_tags()
    {
        var id = await SeedProduct();
        var added = await _store.AddTagsAsync(id, ["dairy", "cold"]);
        Assert.NotEmpty(added);
        Assert.Equal(added.Count, await TagCount(id));
        var entry = await LatestEntry();

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.Equal(0, await TagCount(id));
    }

    [Fact]
    public async Task Undoing_a_substitute_add_removes_them()
    {
        var id = await SeedProduct();
        await _store.AddSubstitutesAsync(id, ["milk", "cream"]);
        Assert.Equal(2, await SubstituteCount(id));
        var entry = await LatestEntry();

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.Equal(0, await SubstituteCount(id));
    }

    [Fact]
    public async Task Undoing_a_grocery_add_removes_them()
    {
        var added = await _store.AddGroceryExtrasAsync(["napkins", "foil"]);
        Assert.Equal(2, added.Count);
        var entry = await LatestEntry();
        Assert.Equal("Added to list: napkins, foil", entry.Summary);

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));
        Assert.Equal(0, await GroceryExtraCount());
    }

    [Fact]
    public async Task Undoing_a_grocery_add_reports_Gone_when_already_removed()
    {
        await _store.AddGroceryExtrasAsync(["napkins"]);
        var entry = await LatestEntry();
        await DeleteAllGroceryExtras(); // removed by another path — nothing left to undo

        Assert.Equal(UndoOutcome.Gone, await _log.UndoAsync(entry.Id));
    }

    // ---- helpers ----

    private async Task<int> SeedProduct(
        string name = "Whole Milk", bool tracked = true, decimal? count = null, DateTimeOffset? countedAt = null)
    {
        await using var db = _db.CreateDbContext();
        var p = new Product { Name = name, IsTracked = tracked };
        if (count is { } c)
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = c;
            p.QuantityCountedAt = countedAt ?? DateTimeOffset.Now.AddDays(-10);
        }
        db.Products.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private async Task<ActivityEntry> LatestEntry()
    {
        await using var db = _db.CreateDbContext();
        return await db.ActivityEntries.AsNoTracking().OrderByDescending(e => e.Id).FirstAsync();
    }

    private async Task<ActivityEntry?> Entry(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.ActivityEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
    }

    private async Task<Product?> ReadProduct(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    private async Task<int> PurchaseCount(int productId)
    {
        await using var db = _db.CreateDbContext();
        return await db.PurchaseEvents.CountAsync(p => p.ProductId == productId);
    }

    private async Task<int> OnlyPurchaseId(int productId)
    {
        await using var db = _db.CreateDbContext();
        return (await db.PurchaseEvents.AsNoTracking().SingleAsync(p => p.ProductId == productId)).Id;
    }

    private async Task DeleteAllPurchases(int productId)
    {
        await using var db = _db.CreateDbContext();
        await db.PurchaseEvents.Where(p => p.ProductId == productId).ExecuteDeleteAsync();
    }

    private async Task ReAttestCount(int productId, decimal count, DateTimeOffset at)
    {
        await using var db = _db.CreateDbContext();
        var p = await db.Products.FirstAsync(x => x.Id == productId);
        p.TrackQuantity = true;
        p.QuantityOnHand = count;
        p.QuantityCountedAt = at;
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedPurchase(int productId, decimal qty = 1, string? brand = null)
    {
        await using var db = _db.CreateDbContext();
        var p = new PurchaseEvent
        {
            ProductId = productId, PurchasedAt = new DateOnly(2026, 8, 1),
            Quantity = qty, Brand = brand, Source = PurchaseSource.Manual,
        };
        db.PurchaseEvents.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private async Task<PurchaseEvent> OnlyPurchase(int productId)
    {
        await using var db = _db.CreateDbContext();
        return await db.PurchaseEvents.AsNoTracking().SingleAsync(p => p.ProductId == productId);
    }

    private async Task<int> SignalCount(int productId)
    {
        await using var db = _db.CreateDbContext();
        return await db.InventorySignals.CountAsync(s => s.ProductId == productId);
    }

    private async Task DeleteAllSignals(int productId)
    {
        await using var db = _db.CreateDbContext();
        await db.InventorySignals.Where(s => s.ProductId == productId).ExecuteDeleteAsync();
    }

    private async Task<bool> ProductExists(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Products.AnyAsync(p => p.Id == id);
    }

    private async Task<int> TagCount(int productId)
    {
        await using var db = _db.CreateDbContext();
        return await db.ProductTags.CountAsync(t => t.ProductId == productId);
    }

    private async Task<int> SubstituteCount(int productId)
    {
        await using var db = _db.CreateDbContext();
        return await db.ProductSubstitutes.CountAsync(s => s.ProductId == productId);
    }

    private async Task<int> GroceryExtraCount()
    {
        await using var db = _db.CreateDbContext();
        return await db.GroceryExtras.CountAsync();
    }

    private async Task DeleteAllGroceryExtras()
    {
        await using var db = _db.CreateDbContext();
        await db.GroceryExtras.ExecuteDeleteAsync();
    }
}
