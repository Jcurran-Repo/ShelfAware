using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

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
    private readonly ProductMergeService _service;

    public ProductMergeServiceTests() => _service = new ProductMergeService(_db);

    public void Dispose() => _db.Dispose();

    private async Task<(int SourceId, int TargetId)> SeedSplitDrinkMix()
    {
        await using var db = _db.CreateDbContext();
        var strawberry = new Product
        {
            Name = "Strawberry Drink Mix",
            IsTracked = true,
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

    [Theory]
    [InlineData("Strawberry Drink Mix", "Drink Mix", "Strawberry")]
    [InlineData("Gala Apples", "Apple", "Gala")] // plural fold: Apples ≈ Apple
    [InlineData("Drink Mix", "Drink Mix", null)] // nothing left over
    [InlineData("Strawberry Drink Mix", "Cat Litter", null)] // no shared words — the leftover isn't a flavor
    public void Suggests_the_variety_label_from_the_name_diff(string source, string target, string? expected) =>
        Assert.Equal(expected, ProductMergeService.SuggestVarietyLabel(source, target));
}
