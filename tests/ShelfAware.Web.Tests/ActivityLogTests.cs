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

    // ---- ProductRenamed (a service-layer action) ----

    [Fact]
    public async Task Renaming_a_product_logs_an_undoable_entry_and_undo_restores_the_name()
    {
        var id = await SeedProduct("Whole Milk");
        await new ProductRenameService(_db, _log).RenameAsync(id, "2% Milk");

        var entry = await LatestEntry();
        Assert.Equal("Renamed Whole Milk to 2% Milk", entry.Summary);
        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));

        Assert.Equal("Whole Milk", (await ReadProduct(id))!.Name);
    }

    [Fact]
    public async Task Undoing_a_rename_re_points_recipe_links_back()
    {
        var id = await SeedProduct("Whole Milk");
        await SeedRecipeIngredient("milk", matchedProduct: "Whole Milk");
        await new ProductRenameService(_db, _log).RenameAsync(id, "2% Milk"); // re-points the link to 2% Milk

        var entry = await LatestEntry();
        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entry.Id));

        Assert.Equal("Whole Milk", await OnlyIngredientMatchedProduct()); // the link followed the rename back
    }

    [Fact]
    public async Task Undoing_a_rename_refuses_after_a_later_rename()
    {
        var id = await SeedProduct("Whole Milk");
        var rename = new ProductRenameService(_db, _log);
        await rename.RenameAsync(id, "2% Milk");
        var entry = await LatestEntry();
        await rename.RenameAsync(id, "Skim Milk"); // renamed again since

        Assert.Equal(UndoOutcome.Superseded, await _log.UndoAsync(entry.Id));
        Assert.Equal("Skim Milk", (await ReadProduct(id))!.Name); // untouched
    }

    [Fact]
    public async Task Undoing_a_rename_refuses_if_the_old_name_is_taken_now()
    {
        var id = await SeedProduct("Whole Milk");
        await new ProductRenameService(_db, _log).RenameAsync(id, "2% Milk");
        var entry = await LatestEntry();
        await SeedProduct("Whole Milk"); // another product now holds the old name — restoring would collide

        Assert.Equal(UndoOutcome.Superseded, await _log.UndoAsync(entry.Id));
        Assert.Equal("2% Milk", (await ReadProduct(id))!.Name); // can't restore — stays put
    }

    // ---- MealLogged (a page-orchestrated action; recording replicated by LogMeal) ----

    [Fact]
    public async Task Logging_a_meal_records_an_undoable_entry_and_undo_reverses_all_three_writes()
    {
        var productId = await SeedProduct("Ground Beef", count: 3, countedAt: DateTimeOffset.Now.AddDays(-3));
        var recipeId = await SeedRecipeWithMain("Tacos", "ground beef", "Ground Beef");
        var (_, entryId, _) = await LogMeal(recipeId, "Tacos");
        Assert.Equal(2m, (await ReadProduct(productId))!.QuantityOnHand); // one package off on the way in

        var entry = (await Entry(entryId))!;
        Assert.Equal(ActivityKind.MealLogged, entry.Kind);
        Assert.Equal(Reversibility.Reversible, entry.Reversibility);
        Assert.Equal("Logged a meal: Tacos", entry.Summary);

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entryId));

        Assert.Equal(0, await MealEventCount());                          // the dated event is gone
        Assert.Equal(0, await RecipeTimesEaten(recipeId));               // the counter stepped back
        Assert.Equal(3m, (await ReadProduct(productId))!.QuantityOnHand); // the taken package returned
        Assert.NotNull((await Entry(entryId))!.UndoneAt);               // and the entry is stamped
    }

    [Fact]
    public async Task Undoing_a_logged_meal_whose_event_is_gone_reports_Gone_and_restores_nothing()
    {
        var productId = await SeedProduct("Ground Beef", count: 3, countedAt: DateTimeOffset.Now.AddDays(-3));
        var recipeId = await SeedRecipeWithMain("Tacos", "ground beef", "Ground Beef");
        var (mealId, entryId, _) = await LogMeal(recipeId, "Tacos");
        await DeleteMealEvent(mealId); // another surface removed it (the inline undo, another device)

        Assert.Equal(UndoOutcome.Gone, await _log.UndoAsync(entryId));
        Assert.Null((await Entry(entryId))!.UndoneAt); // nothing reversed → not stamped
        // Critically, the count is NOT put back: the meal is gone (whoever removed it already restored its
        // stock), so restoring here would invent a package. The handler must check the meal BEFORE restoring.
        Assert.Equal(2m, (await ReadProduct(productId))!.QuantityOnHand);
    }

    [Fact]
    public async Task Undo_after_a_restated_pick_restores_the_picked_package_too()
    {
        // A picker answer lands AFTER the meal's first commit; the page restates the entry so its durable
        // record includes the pick. Without that, a later undo would restore only the auto-take and leave
        // the picked package wrongly deducted — the whole reason Restate exists.
        var autoId = await SeedProduct("Rice", count: 5, countedAt: DateTimeOffset.Now.AddDays(-3));
        var pickId = await SeedProduct("Ground Beef", count: 4, countedAt: DateTimeOffset.Now.AddDays(-3));
        var recipeId = await SeedRecipeWithMain("Tacos", "rice", "Rice"); // grounded to Rice → Rice auto-takes
        var (mealId, entryId, taken) = await LogMeal(recipeId, "Tacos");
        Assert.Equal(4m, (await ReadProduct(pickId))!.QuantityOnHand); // the pick hasn't happened yet

        // The picker answer: take one Ground Beef and restate the entry to include it (what PickCandidate does).
        await using (var db = _db.CreateDbContext())
        {
            var beef = await db.Products.Include(p => p.Purchases).FirstAsync(p => p.Id == pickId);
            var picked = MealStock.TakeOne(beef)!;
            var entry = await db.ActivityEntries.FindAsync(entryId);
            _log.Restate(entry!, new MealLoggedPayload(mealId, "Tacos", [.. taken, picked]));
            await db.SaveChangesAsync(); // the take and the payload restate, one commit
        }
        Assert.Equal(3m, (await ReadProduct(pickId))!.QuantityOnHand); // the pick took one

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entryId));
        Assert.Equal(5m, (await ReadProduct(autoId))!.QuantityOnHand); // auto-take restored
        Assert.Equal(4m, (await ReadProduct(pickId))!.QuantityOnHand); // AND the restated pick restored
    }

    // ---- ReceiptConfirmed (recorded in ReceiptConfirmationService; undo = a total removal + image cleanup) ----

    [Fact]
    public async Task Confirming_a_receipt_records_an_undoable_entry_and_undo_removes_everything_and_the_image()
    {
        var cleanup = new RecordingImageCleanup();
        var log = UndoTesting.Log(_db, cleanup);
        var confirmer = new ReceiptConfirmationService(_db, log);
        var receiptId = await SeedPendingReceipt("receipt-img-1", ("GV MILK", "Whole Milk"));
        await confirmer.ConfirmAsync(receiptId, new DateOnly(2026, 8, 17),
            [Line("GV MILK", "Whole Milk")], writeAliases: true);

        var entry = await LatestEntry();
        Assert.Equal(ActivityKind.ReceiptConfirmed, entry.Kind);
        Assert.Equal("Confirmed 1 item from Walmart", entry.Summary);
        Assert.Equal(1, await PurchaseCountAll());
        Assert.True(await ProductExistsByName("Whole Milk"));

        Assert.Equal(UndoOutcome.Done, await log.UndoAsync(entry.Id));

        Assert.Equal(0, await PurchaseCountAll());              // the purchase it recorded is gone
        Assert.False(await ProductExistsByName("Whole Milk"));  // the product it introduced is gone
        Assert.Equal(0, await ReceiptCount());                  // the receipt row is gone
        Assert.NotNull((await Entry(entry.Id))!.UndoneAt);      // and the entry is stamped
        Assert.Equal(["receipt-img-1"], cleanup.Deleted);       // the image folder was forgotten, after the commit
    }

    [Fact]
    public async Task Peeking_a_receipt_confirm_is_undoable_but_removes_nothing_and_never_touches_the_image()
    {
        // ⚠️ THE trap this whole design exists to avoid: Peek re-runs the reversal to decide whether to grey
        // the /history row. It must remove nothing — and above all must NOT delete the receipt image, which
        // rendering the row would otherwise do to every confirmed receipt in the log.
        var cleanup = new RecordingImageCleanup();
        var log = UndoTesting.Log(_db, cleanup);
        var confirmer = new ReceiptConfirmationService(_db, log);
        var receiptId = await SeedPendingReceipt("receipt-img-2", ("GV MILK", "Whole Milk"));
        await confirmer.ConfirmAsync(receiptId, new DateOnly(2026, 8, 17),
            [Line("GV MILK", "Whole Milk")], writeAliases: true);
        var entry = await LatestEntry();

        Assert.Equal(UndoOutcome.Done, await log.PeekAsync(entry.Id)); // it IS undoable...

        Assert.Equal(1, await ReceiptCount());                 // ...but the peek removed nothing,
        Assert.Equal(1, await PurchaseCountAll());
        Assert.Empty(cleanup.Deleted);                         // deleted no image (the post-commit hook never ran),
        Assert.Null((await Entry(entry.Id))!.UndoneAt);        // and left the entry live
    }

    [Fact]
    public async Task Undoing_a_receipt_confirm_whose_receipt_is_already_gone_reports_Gone()
    {
        var cleanup = new RecordingImageCleanup();
        var log = UndoTesting.Log(_db, cleanup);
        var confirmer = new ReceiptConfirmationService(_db, log);
        var receiptId = await SeedPendingReceipt("receipt-img-3", ("GV MILK", "Whole Milk"));
        await confirmer.ConfirmAsync(receiptId, new DateOnly(2026, 8, 17),
            [Line("GV MILK", "Whole Milk")], writeAliases: true);
        var entry = await LatestEntry();
        await DeleteReceipt(receiptId); // the Upload page's ↩ Undo (or the Receipts page) already removed it

        Assert.Equal(UndoOutcome.Gone, await log.UndoAsync(entry.Id));
        Assert.Null((await Entry(entry.Id))!.UndoneAt); // nothing reversed → not stamped
        Assert.Empty(cleanup.Deleted);                  // and no image delete for a receipt that was already gone
    }

    // ---- history-only (recorded, greyed, never reversed): ProductsMerged, CensusConfirmed ----

    [Fact]
    public async Task Merging_products_records_a_history_only_entry_that_cannot_be_undone()
    {
        var sourceId = await SeedProduct("Strawberry Drink Mix");
        var targetId = await SeedProduct("Drink Mix");
        await new ProductMergeService(_db, _log).MergeAsync(sourceId, targetId);

        var entry = await LatestEntry();
        Assert.Equal(ActivityKind.ProductsMerged, entry.Kind);
        Assert.Equal(Reversibility.NotReversible, entry.Reversibility);
        Assert.Equal("Merged Strawberry Drink Mix into Drink Mix", entry.Summary);

        Assert.Equal(UndoOutcome.NotReversible, await _log.UndoAsync(entry.Id)); // greyed — refused before dispatch
        Assert.Null((await Entry(entry.Id))!.UndoneAt);                          // and never stamped
    }

    [Fact]
    public async Task A_census_records_a_history_only_entry_that_cannot_be_undone()
    {
        var productId = await SeedProduct("Canned Beans");
        await new CensusConfirmationService(_db, _log).ConfirmAsync(
            [new CensusConfirmationService.CensusRow("Canned Beans", Category.Pantry, 5m, productId)]);

        var entry = await LatestEntry();
        Assert.Equal(ActivityKind.CensusConfirmed, entry.Kind);
        Assert.Equal(Reversibility.NotReversible, entry.Reversibility);
        Assert.Equal("Counted 1 item from a photo", entry.Summary);

        Assert.Equal(UndoOutcome.NotReversible, await _log.UndoAsync(entry.Id));
        Assert.Null((await Entry(entry.Id))!.UndoneAt);
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

    private async Task SeedRecipeIngredient(string ingredientName, string matchedProduct)
    {
        await using var db = _db.CreateDbContext();
        db.Recipes.Add(new Recipe
        {
            Name = "Test Recipe",
            SavedAt = DateTimeOffset.Now,
            Ingredients = [new RecipeIngredient { Name = ingredientName, IsMain = true, MatchedProduct = matchedProduct }],
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> OnlyIngredientMatchedProduct()
    {
        await using var db = _db.CreateDbContext();
        return (await db.RecipeIngredients.AsNoTracking().SingleAsync()).MatchedProduct;
    }

    private async Task<int> SeedRecipeWithMain(string recipeName, string mainName, string? matchedProduct)
    {
        await using var db = _db.CreateDbContext();
        var recipe = new Recipe
        {
            Name = recipeName,
            SavedAt = DateTimeOffset.Now,
            Ingredients = [new RecipeIngredient { Name = mainName, IsMain = true, MatchedProduct = matchedProduct }],
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    /// <summary>Replicates the page's MarkEaten DB effect (Recipes.razor): bump TimesEaten, create a dated
    /// MealEvent, take one package off each counted main via <see cref="MealStock"/>, and record the
    /// MealLogged entry inside one transaction (the entry keys on the meal's generated id). Returns the meal
    /// id, the entry id, and the actual takes — everything a MealLogged undo reverses.</summary>
    private async Task<(int MealEventId, int EntryId, IReadOnlyList<MealStock.Applied> Taken)> LogMeal(
        int recipeId, string recipeName)
    {
        await using var db = _db.CreateDbContext();
        var recipe = await db.Recipes.Include(r => r.Ingredients).FirstAsync(r => r.Id == recipeId);
        recipe.TimesEaten++;
        var resolution = await MealStock.ResolveAsync(db, recipe);
        var meal = new MealEvent { RecipeId = recipeId, AteAt = new DateOnly(2026, 8, 17) };
        db.MealEvents.Add(meal);
        var taken = MealStock.Apply(resolution);

        ActivityEntry entry;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.SaveChangesAsync(); // assigns meal.Id + the counter/take on the same commit
            entry = _log.Record(db, ActivityKind.MealLogged, new MealLoggedPayload(meal.Id, recipeName, taken.ToList()));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return (meal.Id, entry.Id, taken);
    }

    private async Task<int> MealEventCount()
    {
        await using var db = _db.CreateDbContext();
        return await db.MealEvents.CountAsync();
    }

    private async Task<int> RecipeTimesEaten(int recipeId)
    {
        await using var db = _db.CreateDbContext();
        return (await db.Recipes.AsNoTracking().SingleAsync(r => r.Id == recipeId)).TimesEaten;
    }

    private async Task DeleteMealEvent(int mealEventId)
    {
        await using var db = _db.CreateDbContext();
        await db.MealEvents.Where(m => m.Id == mealEventId).ExecuteDeleteAsync();
    }

    /// <summary>A confirm line for a NEW product (ProductId 0 → the confirm creates and introduces it).</summary>
    private static ReceiptConfirmationService.ConfirmLine Line(string raw, string name) =>
        new(raw, name, null, null, null, 1, Category.Pantry, [], 0);

    /// <summary>A pending receipt as an upload leaves it — Walmart, an image folder, and its lines — ready
    /// for <see cref="ReceiptConfirmationService"/>.</summary>
    private async Task<int> SeedPendingReceipt(string imagePath, params (string Raw, string Name)[] lines)
    {
        await using var db = _db.CreateDbContext();
        var receipt = new Receipt
        {
            Merchant = "Walmart",
            PurchasedAt = new DateOnly(2026, 8, 17),
            ImagePath = imagePath,
            Lines = lines.Select(l => new ReceiptLine
            {
                RawText = l.Raw, NormalizedName = l.Name, Quantity = 1, Confidence = 0.9m,
            }).ToList(),
        };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    private async Task<int> PurchaseCountAll()
    {
        await using var db = _db.CreateDbContext();
        return await db.PurchaseEvents.CountAsync();
    }

    private async Task<int> ReceiptCount()
    {
        await using var db = _db.CreateDbContext();
        return await db.Receipts.CountAsync();
    }

    private async Task<bool> ProductExistsByName(string name)
    {
        await using var db = _db.CreateDbContext();
        return await db.Products.AnyAsync(p => p.Name == name);
    }

    /// <summary>Remove a receipt the way the Upload/Receipts pages do — through the same DB reversal the
    /// undo would run — so the "already gone" scenario is exactly what a real prior removal leaves (FKs and
    /// all), not a bare row-delete that orphans its purchases.</summary>
    private async Task DeleteReceipt(int receiptId)
    {
        await using var db = _db.CreateDbContext();
        await ReceiptRemovalService.RemoveOnAsync(db, receiptId);
        await db.SaveChangesAsync();
    }
}
