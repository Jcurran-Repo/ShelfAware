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
        var purchaseId = await OnlyPurchaseId(id);
        await _store.SetPurchaseQuantityAsync(purchaseId, 3); // a later action changed the same row

        var entry = await LatestEntry();
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
}
