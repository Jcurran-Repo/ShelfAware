using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;
using ShelfAware.Web.Undo;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The merge is the Variety feature's repair path for history: products that predate variety
/// tracking carry the flavor in their NAME ("Strawberry Drink Mix") and can never roll up on their
/// own. These run on real SQLite with FK enforcement because the ordering is the risky part —
/// purchases/signals/tags cascade on product delete and ReceiptLine.ProductId has no delete action
/// (see ProductDeletionTests), so a merge that deleted before re-pointing would eat the history.
/// </summary>
public class ProductMergeServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ActivityLogService _log;
    private readonly ProductMergeService _service;

    public ProductMergeServiceTests()
    {
        _log = UndoTesting.Log(_db);
        _service = new ProductMergeService(_db, _log);
    }

    public void Dispose() => _db.Dispose();

    private async Task<int> LatestMergeEntryId()
    {
        await using var db = _db.CreateDbContext();
        return (await db.ActivityEntries.OrderByDescending(e => e.Id).FirstAsync()).Id;
    }

    private async Task<(int SourceId, int TargetId)> SeedSplitDrinkMix()
    {
        await using var db = _db.CreateDbContext();
        var strawberry = new Product
        {
            Name = "Strawberry Drink Mix",
            IsTracked = true,
            DefaultUnit = "ct", // the target has none, so the merge fills it — and the undo must clear it again
            Tags = [new ProductTag { Value = "Snack" }, new ProductTag { Value = "Powdered" }],
            Substitutes = [new ProductSubstitute { Value = "fruit drink" }],
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = new DateOnly(2026, 6, 1), Brand = "Kool-Aid" },
                new PurchaseEvent { PurchasedAt = new DateOnly(2026, 6, 15), Brand = "Kool-Aid" },
            ],
            Signals = [new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = DateTimeOffset.Now }],
        };
        var target = new Product
        {
            Name = "Drink Mix",
            IsTracked = false, // the merged product must come out tracked because the source was
            Tags = [new ProductTag { Value = "Snack" }],
            Purchases = [new PurchaseEvent { PurchasedAt = new DateOnly(2026, 7, 1), Brand = "Crystal Light", Variety = "Grape" }],
        };
        var receipt = new Receipt
        {
            Merchant = "Walmart",
            ImagePath = "receipts/test",
            Status = ReceiptStatus.Confirmed,
            Lines = [new ReceiptLine { RawText = "KOOL AID STRAW", NormalizedName = "Strawberry Drink Mix", Product = strawberry }],
        };
        db.Products.AddRange(strawberry, target);
        db.Receipts.Add(receipt);
        db.ProductAliases.Add(new ProductAlias { Merchant = "Walmart", RawText = "KOOL AID STRAW", Product = strawberry });
        db.Recipes.Add(new Recipe
        {
            Name = "Summer Punch",
            Ingredients = [new RecipeIngredient { Name = "Drink mix", IsMain = true, MatchedProduct = "strawberry drink mix" }],
        });
        await db.SaveChangesAsync();
        return (strawberry.Id, target.Id);
    }

    [Fact]
    public async Task Moves_history_labels_variety_and_deletes_the_source()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();

        var result = await _service.MergeAsync(sourceId, targetId, "Strawberry");

        Assert.True(result.Ok);
        Assert.Equal(2, result.MovedPurchases);
        Assert.Equal(1, result.RelinkedIngredients);

        await using var db = _db.CreateDbContext();
        Assert.Null(await db.Products.FirstOrDefaultAsync(p => p.Id == sourceId));

        var purchases = await db.PurchaseEvents.Where(p => p.ProductId == targetId).ToListAsync();
        Assert.Equal(3, purchases.Count);
        // Moved purchases get the label; the target's own purchase keeps the variety it already had.
        Assert.Equal(2, purchases.Count(p => p.Variety == "Strawberry"));
        Assert.Equal(1, purchases.Count(p => p.Variety == "Grape"));

        var line = await db.ReceiptLines.SingleAsync();
        Assert.Equal(targetId, line.ProductId);
        Assert.Equal("Strawberry", line.Variety);

        Assert.Equal(targetId, (await db.ProductAliases.SingleAsync()).ProductId);
        Assert.Equal(targetId, (await db.InventorySignals.SingleAsync()).ProductId);

        // Tags union (shared "Snack" not duplicated), substitutes come along, recipe link re-points,
        // and tracking survives from the source.
        var target = await db.Products.Include(p => p.Tags).Include(p => p.Substitutes).SingleAsync(p => p.Id == targetId);
        Assert.Equal(["Powdered", "Snack"], target.Tags.Select(t => t.Value).OrderBy(v => v));
        Assert.Equal("fruit drink", target.Substitutes.Single().Value);
        Assert.True(target.IsTracked);
        Assert.Equal("Drink Mix", (await db.RecipeIngredients.SingleAsync()).MatchedProduct);
    }

    [Fact]
    public async Task Merge_without_a_label_leaves_existing_varieties_alone()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();

        var result = await _service.MergeAsync(sourceId, targetId, varietyForMoved: "   ");

        Assert.True(result.Ok);
        await using var db = _db.CreateDbContext();
        var purchases = await db.PurchaseEvents.Where(p => p.ProductId == targetId).ToListAsync();
        Assert.Equal(2, purchases.Count(p => p.Variety == null));
        Assert.Equal(1, purchases.Count(p => p.Variety == "Grape"));
    }

    [Fact]
    public async Task Refuses_self_merge_and_missing_products()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();

        Assert.False((await _service.MergeAsync(sourceId, sourceId)).Ok);
        Assert.False((await _service.MergeAsync(sourceId, 99999)).Ok);
        Assert.False((await _service.MergeAsync(99999, targetId)).Ok);

        await using var db = _db.CreateDbContext();
        Assert.Equal(2, await db.Products.CountAsync()); // nothing merged, nothing deleted
    }

    [Fact]
    public async Task Cannot_merge_into_another_households_product()
    {
        var (sourceId, _) = await SeedSplitDrinkMix();

        _db.HouseholdId = "hh-other";
        int foreignId;
        await using (var other = _db.CreateDbContext())
        {
            var foreign = new Product { Name = "Drink Mix" };
            other.Products.Add(foreign);
            await other.SaveChangesAsync();
            foreignId = foreign.Id;
        }
        _db.HouseholdId = "hh-test";

        // The scoped context can't see the other household's product, so the merge fails to find it
        // — in either direction — and our data is untouched.
        Assert.False((await _service.MergeAsync(sourceId, foreignId)).Ok);
        Assert.False((await _service.MergeAsync(foreignId, sourceId)).Ok);

        await using var db = _db.CreateDbContext();
        Assert.NotNull(await db.Products.FirstOrDefaultAsync(p => p.Id == sourceId));
        Assert.Equal(2, await db.PurchaseEvents.CountAsync(p => p.ProductId == sourceId));
    }

    [Fact]
    public async Task Relinks_a_recipe_ingredient_whose_MatchedProduct_differs_only_in_PUNCTUATION()
    {
        // ⚠️ The recipe re-point keys on the product NAME, and a MatchedProduct stored as "Home Canned
        // Sauce" for a source named "Home-Canned Sauce" is the same product to every other guard — so a
        // raw ToLower() compare left that link stale and the source delete below then ORPHANED it. Same
        // fix and reason as ProductRenameService's punctuation relink; the merge path was its un-converted
        // sibling (its own comment already claims "Same rule as ProductRenameService").
        int sourceId, targetId;
        await using (var db = _db.CreateDbContext())
        {
            var source = new Product { Name = "Home-Canned Sauce" };
            var target = new Product { Name = "Sauce" };
            db.Products.AddRange(source, target);
            db.Recipes.Add(new Recipe
            {
                Name = "Pasta",
                Ingredients = [new RecipeIngredient { Name = "Sauce", IsMain = true, MatchedProduct = "Home Canned Sauce" }],
            });
            await db.SaveChangesAsync();
            (sourceId, targetId) = (source.Id, target.Id);
        }

        var result = await _service.MergeAsync(sourceId, targetId);

        Assert.True(result.Ok);
        Assert.Equal(1, result.RelinkedIngredients);
        await using var read = _db.CreateDbContext();
        // Re-pointed to the target's name, not left stranded on the deleted source's punctuation variant.
        Assert.Equal("Sauce", (await read.RecipeIngredients.SingleAsync()).MatchedProduct);
    }

    [Fact]
    public async Task Merging_away_a_junk_named_product_does_not_repoint_other_junk_links()
    {
        // The merge is the other sanctioned repair for a pre-guard junk-named product ("!!" folded
        // into a real item, junk row deleted) — but its old IdentityKey is "", which every OTHER
        // identity-less MatchedProduct folds to as well, so an ungated re-point would drag the whole
        // empty-key equivalence class onto the target's name. RecipeLinks (shared with the rename)
        // must touch nothing instead: an empty key can't say WHICH junk product a link meant.
        int sourceId, targetId;
        await using (var db = _db.CreateDbContext())
        {
            var source = new Product { Name = "!!" }; // pre-guard state, seeded directly
            var target = new Product { Name = "Sardines" };
            db.Products.AddRange(source, target);
            db.Recipes.Add(new Recipe
            {
                Name = "Mystery Stew",
                Ingredients = [new RecipeIngredient { Name = "Something", IsMain = true, MatchedProduct = "--" }],
            });
            await db.SaveChangesAsync();
            (sourceId, targetId) = (source.Id, target.Id);
        }

        var result = await _service.MergeAsync(sourceId, targetId);

        Assert.True(result.Ok); // the repair itself must keep working — junk row gone, history pooled
        Assert.Equal(0, result.RelinkedIngredients);
        await using var read = _db.CreateDbContext();
        Assert.Equal("--", (await read.RecipeIngredients.SingleAsync()).MatchedProduct);
        Assert.Null(await read.Products.FirstOrDefaultAsync(p => p.Id == sourceId));
    }

    [Theory]
    [InlineData("Strawberry Drink Mix", "Drink Mix", "Strawberry")]
    [InlineData("Gala Apples", "Apple", "Gala")] // plural fold: Apples ≈ Apple
    [InlineData("Drink Mix", "Drink Mix", null)] // nothing left over
    [InlineData("Strawberry Drink Mix", "Cat Litter", null)] // no shared words — the leftover isn't a flavor
    public void Suggests_the_variety_label_from_the_name_diff(string source, string target, string? expected) =>
        Assert.Equal(expected, ProductMergeService.SuggestVarietyLabel(source, target));

    // ---- undo: rebuild the deleted source and pull its contribution back out of the survivor ----

    [Fact]
    public async Task Undo_rebuilds_the_source_and_pulls_its_history_back_out()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();
        await _service.MergeAsync(sourceId, targetId, "Strawberry");
        var entryId = await LatestMergeEntryId();

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entryId));

        await using var db = _db.CreateDbContext();
        // The source is back — a fresh row (new id) with its full definition and its own children restored.
        var source = await db.Products.Include(p => p.Tags).Include(p => p.Substitutes)
            .SingleAsync(p => p.Name == "Strawberry Drink Mix");
        Assert.True(source.IsTracked);
        Assert.Equal("ct", source.DefaultUnit);
        Assert.Equal(["Powdered", "Snack"], source.Tags.Select(t => t.Value).OrderBy(v => v));
        Assert.Equal("fruit drink", source.Substitutes.Single().Value);

        // Its purchases / line / alias / signal moved back — and the variety the merge stamped is gone again.
        var sourcePurchases = await db.PurchaseEvents.Where(p => p.ProductId == source.Id).ToListAsync();
        Assert.Equal(2, sourcePurchases.Count);
        Assert.All(sourcePurchases, p => Assert.Null(p.Variety));
        var line = await db.ReceiptLines.SingleAsync();
        Assert.Equal(source.Id, line.ProductId);
        Assert.Null(line.Variety);
        Assert.Equal(source.Id, (await db.ProductAliases.SingleAsync()).ProductId);
        Assert.Equal(source.Id, (await db.InventorySignals.SingleAsync()).ProductId);

        // The target is back to exactly what it had before the merge: its own purchase, one tag, no
        // substitute, untracked, no unit.
        var target = await db.Products.Include(p => p.Tags).Include(p => p.Substitutes).SingleAsync(p => p.Id == targetId);
        Assert.False(target.IsTracked);
        Assert.Null(target.DefaultUnit);
        Assert.Equal(["Snack"], target.Tags.Select(t => t.Value));
        Assert.Empty(target.Substitutes);
        var targetPurchases = await db.PurchaseEvents.Where(p => p.ProductId == targetId).ToListAsync();
        Assert.Equal("Grape", targetPurchases.Single().Variety); // its own purchase untouched

        // The recipe link points back at the source (its canonical name — identity-equal to the original link).
        Assert.Equal("Strawberry Drink Mix", (await db.RecipeIngredients.SingleAsync()).MatchedProduct);
    }

    [Fact]
    public async Task Undo_of_an_unlabelled_merge_restores_the_moved_purchases_with_no_variety()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();
        await _service.MergeAsync(sourceId, targetId); // no label → nothing was stamped, nothing to un-stamp
        var entryId = await LatestMergeEntryId();

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entryId));

        await using var db = _db.CreateDbContext();
        var source = await db.Products.SingleAsync(p => p.Name == "Strawberry Drink Mix");
        var purchases = await db.PurchaseEvents.Where(p => p.ProductId == source.Id).ToListAsync();
        Assert.Equal(2, purchases.Count);
        Assert.All(purchases, p => Assert.Null(p.Variety));
    }

    [Fact]
    public async Task Undo_revives_what_survives_when_a_moved_purchase_was_deleted_since()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();
        await _service.MergeAsync(sourceId, targetId, "Strawberry");
        var entryId = await LatestMergeEntryId();

        // One merged purchase is deleted meanwhile (e.g. a receipt removal). The undo can't resurrect it —
        // it revives what's still there rather than refusing the whole reversal.
        await using (var db = _db.CreateDbContext())
        {
            var doomed = await db.PurchaseEvents
                .Where(p => p.ProductId == targetId && p.Variety == "Strawberry").FirstAsync();
            db.PurchaseEvents.Remove(doomed);
            await db.SaveChangesAsync();
        }

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entryId));

        await using var read = _db.CreateDbContext();
        var source = await read.Products.SingleAsync(p => p.Name == "Strawberry Drink Mix");
        Assert.Equal(1, await read.PurchaseEvents.CountAsync(p => p.ProductId == source.Id)); // only the survivor
    }

    [Fact]
    public async Task Undo_refuses_when_the_target_was_itself_merged_away_since()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();
        await _service.MergeAsync(sourceId, targetId, "Strawberry");
        var firstMergeEntryId = await LatestMergeEntryId();

        // The survivor is then folded into a third product — targetId no longer exists to un-merge from.
        int thirdId;
        await using (var db = _db.CreateDbContext())
        {
            var third = new Product { Name = "Beverage Mix" };
            db.Products.Add(third);
            await db.SaveChangesAsync();
            thirdId = third.Id;
        }
        await _service.MergeAsync(targetId, thirdId);

        Assert.Equal(UndoOutcome.Gone, await _log.UndoAsync(firstMergeEntryId));
        await using var read = _db.CreateDbContext();
        Assert.Null(await read.Products.FirstOrDefaultAsync(p => p.Name == "Strawberry Drink Mix")); // not revived
    }

    [Fact]
    public async Task Undo_of_a_twin_merge_revives_the_source_beside_the_target()
    {
        // Folding two identity-TWINS is a primary use of the merge tool (punctuation folds — "Half and Half"
        // and "Half-and-Half" are one product to the matcher). Undoing it faithfully restores the pre-merge
        // state: BOTH come back, each with its own history. The app tolerates twins (the merge tool exists to
        // clean them), so the undo doesn't refuse on the collision — it restores, visibly and recoverably.
        int sourceId, targetId;
        await using (var db = _db.CreateDbContext())
        {
            var source = new Product
            {
                Name = "Half and Half",
                Purchases = [new PurchaseEvent { PurchasedAt = new DateOnly(2026, 6, 1) }],
            };
            var target = new Product
            {
                Name = "Half-and-Half",
                Purchases = [new PurchaseEvent { PurchasedAt = new DateOnly(2026, 6, 5) }],
            };
            db.Products.AddRange(source, target);
            await db.SaveChangesAsync();
            (sourceId, targetId) = (source.Id, target.Id);
        }

        await _service.MergeAsync(sourceId, targetId); // both purchases pool onto the target
        var entryId = await LatestMergeEntryId();

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entryId)); // NOT refused on the twin collision

        await using var read = _db.CreateDbContext();
        // Both twins exist again, histories split back to one purchase each.
        var revived = await read.Products.SingleAsync(p => p.Name == "Half and Half");
        Assert.Equal(1, await read.PurchaseEvents.CountAsync(p => p.ProductId == revived.Id));
        Assert.Equal(1, await read.PurchaseEvents.CountAsync(p => p.ProductId == targetId));
    }

    [Fact]
    public async Task Peek_reports_the_merge_undoable_without_un_merging_then_already_undone()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();
        await _service.MergeAsync(sourceId, targetId, "Strawberry");
        var entryId = await LatestMergeEntryId();

        Assert.Equal(UndoOutcome.Done, await _log.PeekAsync(entryId)); // undoable — but a Peek does NOT do it
        await using (var db = _db.CreateDbContext())
            Assert.Null(await db.Products.FirstOrDefaultAsync(p => p.Name == "Strawberry Drink Mix"));

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entryId));
        Assert.Equal(UndoOutcome.AlreadyUndone, await _log.PeekAsync(entryId)); // spent now
    }

    // ---- rename on merge: the lookalike card's "neither name — call it this" ----

    private async Task<string> LatestSummary()
    {
        await using var db = _db.CreateDbContext();
        return (await db.ActivityEntries.OrderByDescending(e => e.Id).FirstAsync()).Summary;
    }

    [Fact]
    public async Task Merge_with_a_new_name_renames_the_survivor_and_follows_the_recipe_links()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();

        var result = await _service.MergeAsync(sourceId, targetId, renameTo: "  Powdered Drink Mix ");

        Assert.True(result.Ok);
        Assert.Equal("Merged Strawberry Drink Mix and Drink Mix as Powdered Drink Mix", await LatestSummary());
        await using var db = _db.CreateDbContext();
        Assert.Null(await db.Products.FirstOrDefaultAsync(p => p.Id == sourceId));
        Assert.Equal("Powdered Drink Mix", (await db.Products.SingleAsync(p => p.Id == targetId)).Name);
        Assert.Equal(3, await db.PurchaseEvents.CountAsync(p => p.ProductId == targetId));
        // The source's recipe link followed it to the target, then the rename carried it to the new name.
        Assert.Equal("Powdered Drink Mix", (await db.RecipeIngredients.SingleAsync()).MatchedProduct);
    }

    [Fact]
    public async Task Merge_may_take_the_source_names_identity_as_the_new_name()
    {
        // ⚠️ The source is going away, so a cleaned-up spelling of ITS name must not be refused as "taken" —
        // the rename's taken-check reads the products table, so the merge has to delete the source first.
        int sourceId, targetId;
        await using (var seed = _db.CreateDbContext())
        {
            var source = new Product { Name = "brioche bread" };
            var target = new Product { Name = "Brioche Bread Loaf" };
            seed.Products.AddRange(source, target);
            await seed.SaveChangesAsync();
            (sourceId, targetId) = (source.Id, target.Id);
        }

        var result = await _service.MergeAsync(sourceId, targetId, renameTo: "Brioche Bread");

        Assert.True(result.Ok, result.Message);
        await using var db = _db.CreateDbContext();
        Assert.Equal("Brioche Bread", (await db.Products.SingleAsync()).Name);
    }

    [Fact]
    public async Task A_refused_new_name_rolls_the_whole_merge_back()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();
        await using (var seed = _db.CreateDbContext())
        {
            seed.Products.Add(new Product { Name = "Lemonade" });
            await seed.SaveChangesAsync();
        }

        var result = await _service.MergeAsync(sourceId, targetId, "Strawberry", renameTo: "lemonade");

        Assert.False(result.Ok);
        Assert.Contains("already exists", result.Message);
        await using var db = _db.CreateDbContext();
        // Nothing half-done: the source and its history are where they were, the target kept its name, the
        // link still names the source, and no history entry claims a merge that didn't happen.
        Assert.Equal(2, await db.PurchaseEvents.CountAsync(p => p.ProductId == sourceId));
        Assert.All(await db.PurchaseEvents.Where(p => p.ProductId == sourceId).ToListAsync(), p => Assert.Null(p.Variety));
        Assert.Equal("Drink Mix", (await db.Products.SingleAsync(p => p.Id == targetId)).Name);
        Assert.Equal("strawberry drink mix", (await db.RecipeIngredients.SingleAsync()).MatchedProduct);
        Assert.Empty(await db.ActivityEntries.ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Drink Mix")] // the target's own name — nothing to rename
    public async Task A_blank_or_unchanged_new_name_is_a_plain_merge(string renameTo)
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();

        Assert.True((await _service.MergeAsync(sourceId, targetId, renameTo: renameTo)).Ok);

        Assert.Equal("Merged Strawberry Drink Mix into Drink Mix", await LatestSummary());
        await using var db = _db.CreateDbContext();
        Assert.Equal("Drink Mix", (await db.Products.SingleAsync(p => p.Id == targetId)).Name);
    }

    [Fact]
    public async Task Undo_of_a_renaming_merge_restores_both_products_and_both_names()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();
        await _service.MergeAsync(sourceId, targetId, "Strawberry", renameTo: "Powdered Drink Mix");
        var entryId = await LatestMergeEntryId();

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entryId));

        await using var db = _db.CreateDbContext();
        Assert.Equal("Drink Mix", (await db.Products.SingleAsync(p => p.Id == targetId)).Name);
        var source = await db.Products.SingleAsync(p => p.Name == "Strawberry Drink Mix");
        Assert.Equal(2, await db.PurchaseEvents.CountAsync(p => p.ProductId == source.Id));
        Assert.Equal(1, await db.PurchaseEvents.CountAsync(p => p.ProductId == targetId));
        // The link went new name → old target name (rename back) → source (un-merge).
        Assert.Equal("Strawberry Drink Mix", (await db.RecipeIngredients.SingleAsync()).MatchedProduct);
    }

    [Fact]
    public async Task Undo_of_a_renaming_merge_keeps_a_name_the_user_changed_since()
    {
        var (sourceId, targetId) = await SeedSplitDrinkMix();
        await _service.MergeAsync(sourceId, targetId, renameTo: "Powdered Drink Mix");
        var entryId = await LatestMergeEntryId();
        Assert.True((await new ProductRenameService(_db, _log).RenameAsync(targetId, "Kool-Aid")).Ok);

        Assert.Equal(UndoOutcome.Done, await _log.UndoAsync(entryId));

        await using var db = _db.CreateDbContext();
        Assert.Equal("Kool-Aid", (await db.Products.SingleAsync(p => p.Id == targetId)).Name); // theirs wins
        Assert.NotNull(await db.Products.FirstOrDefaultAsync(p => p.Name == "Strawberry Drink Mix")); // still un-merged
    }

    [Theory]
    [InlineData("Artesano Brioche Bread", "Brioche Bread Loaf", "Brioche Bread")]
    [InlineData("Gala Apples", "Apple Gala Pack", "Gala")] // longest CONSECUTIVE run, not shared words in any order
    [InlineData("Sharp Cheddar Cheese", "Cheddar Cheeses", "Cheddar Cheese")] // plural fold; first name's spelling
    [InlineData("Hand Soap", "Cat Litter", null)]
    public void Suggests_the_shared_name_from_the_longest_common_run(string first, string second, string? expected) =>
        Assert.Equal(expected, ProductMergeService.SuggestSharedName(first, second));
}
