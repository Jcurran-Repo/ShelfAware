using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The suite that earns multi-tenancy: whatever household A does, household B must not see, touch,
/// or collide with it. All against real SQLite with the real query filters + stamping — one TestDb,
/// re-pointed between households, exactly like two signed-in circuits over one database.
/// </summary>
public class HouseholdIsolationTests : IDisposable
{
    private const string A = "hh-a";
    private const string B = "hh-b";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ShelfAwareDbContext As(string household)
    {
        _db.HouseholdId = household;
        return _db.CreateDbContext();
    }

    private static Product NewProduct(string name) => new()
    {
        Name = name,
        Category = Category.Dairy,
        Purchases = [new PurchaseEvent { PurchasedAt = new DateOnly(2026, 7, 1) }],
        Signals = [new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = DateTimeOffset.Now }],
        Tags = [new ProductTag { Value = "Staple" }],
        Substitutes = [new ProductSubstitute { Value = "milk" }],
    };

    [Fact]
    public async Task Stamping_covers_a_whole_added_graph()
    {
        await using (var db = As(A))
        {
            db.Products.Add(NewProduct("Whole Milk"));
            await db.SaveChangesAsync();
        }

        await using var raw = _db.CreateUnscopedContext();
        Assert.Equal(A, (await raw.Products.IgnoreQueryFilters().SingleAsync()).HouseholdId);
        Assert.Equal(A, (await raw.PurchaseEvents.IgnoreQueryFilters().SingleAsync()).HouseholdId);
        Assert.Equal(A, (await raw.InventorySignals.IgnoreQueryFilters().SingleAsync()).HouseholdId);
        Assert.Equal(A, (await raw.ProductTags.IgnoreQueryFilters().SingleAsync()).HouseholdId);
        Assert.Equal(A, (await raw.ProductSubstitutes.IgnoreQueryFilters().SingleAsync()).HouseholdId);
    }

    [Fact]
    public async Task One_households_rows_are_invisible_to_another()
    {
        await using (var db = As(A))
        {
            db.Products.Add(NewProduct("Whole Milk"));
            db.GroceryExtras.Add(new GroceryExtra { Name = "birthday candles" });
            db.ExcludedFoods.Add(new ExcludedFood { Value = "mushrooms" });
            db.Recipes.Add(new Recipe
            {
                Name = "Milk Toast",
                SavedAt = DateTimeOffset.Now,
                Ingredients = [new RecipeIngredient { Name = "milk", IsMain = true }],
                Steps = [new RecipeStep { Order = 1, Text = "Combine." }],
            });
            await db.SaveChangesAsync();
        }

        await using (var db = As(B))
        {
            Assert.Empty(await db.Products.ToListAsync());
            Assert.Empty(await db.PurchaseEvents.ToListAsync());
            Assert.Empty(await db.InventorySignals.ToListAsync());
            Assert.Empty(await db.GroceryExtras.ToListAsync());
            Assert.Empty(await db.ExcludedFoods.ToListAsync());
            Assert.Empty(await db.Recipes.ToListAsync());
            Assert.Empty(await db.RecipeIngredients.ToListAsync());
            Assert.Empty(await db.RecipeSteps.ToListAsync());
        }

        await using (var db = As(A))
        {
            Assert.Single(await db.Products.Include(p => p.Purchases).ToListAsync());
            Assert.Single(await db.Recipes.Include(r => r.Ingredients).Include(r => r.Steps).ToListAsync());
        }
    }

    [Fact]
    public async Task FindAsync_respects_the_household_filter()
    {
        int productId;
        await using (var db = As(A))
        {
            var product = NewProduct("Whole Milk");
            db.Products.Add(product);
            await db.SaveChangesAsync();
            productId = product.Id;
        }

        // Pins that the EfPantryStore guards can rely on FindAsync: a foreign id resolves to null.
        await using (var db = As(B))
        {
            Assert.Null(await db.Products.FindAsync(productId));
        }
        await using (var db = As(A))
        {
            Assert.NotNull(await db.Products.FindAsync(productId));
        }
    }

    [Fact]
    public async Task Chat_store_writes_nothing_for_a_foreign_product_id()
    {
        int productId;
        await using (var db = As(A))
        {
            var product = NewProduct("Whole Milk");
            db.Products.Add(product);
            await db.SaveChangesAsync();
            productId = product.Id;
        }

        _db.HouseholdId = B;
        var store = new EfPantryStore(_db);
        var retracked = await store.AddPurchaseAsync(productId, new DateOnly(2026, 7, 2), 1);
        await store.RecordSignalAsync(productId, SignalKind.RunningLow);

        Assert.False(retracked);
        await using var raw = _db.CreateUnscopedContext();
        // Still only household A's original seed rows — B's calls added nothing anywhere.
        Assert.Equal(1, await raw.PurchaseEvents.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await raw.InventorySignals.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Confirming_a_foreign_receipt_fails_as_not_found()
    {
        int receiptId;
        await using (var db = As(A))
        {
            var receipt = new Receipt { ImagePath = "receipts/x" };
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync();
            receiptId = receipt.Id;
        }

        _db.HouseholdId = B;
        var confirmer = new ReceiptConfirmationService(_db);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            confirmer.ConfirmAsync(receiptId, new DateOnly(2026, 7, 2), [], writeAliases: true));
        Assert.Contains("no longer exists", ex.Message);
    }

    [Fact]
    public async Task The_same_merchant_alias_coexists_in_two_households()
    {
        await using (var db = As(A))
        {
            db.Products.Add(NewProduct("Whole Milk"));
            await db.SaveChangesAsync();
            db.ProductAliases.Add(new ProductAlias
            {
                Merchant = "Walmart",
                RawText = "GV WHL MLK 1GAL",
                ProductId = (await db.Products.SingleAsync()).Id,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = As(B))
        {
            db.Products.Add(NewProduct("Whole Milk"));
            await db.SaveChangesAsync();
            // The exact same (Merchant, RawText) — legal since uniqueness is per household now.
            db.ProductAliases.Add(new ProductAlias
            {
                Merchant = "Walmart",
                RawText = "GV WHL MLK 1GAL",
                ProductId = (await db.Products.SingleAsync()).Id,
            });
            await db.SaveChangesAsync();

            Assert.Single(await db.ProductAliases.ToListAsync());
        }

        await using var raw = _db.CreateUnscopedContext();
        Assert.Equal(2, await raw.ProductAliases.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Export_and_delete_touch_only_the_current_household()
    {
        await using (var db = As(A))
        {
            db.Products.Add(NewProduct("Whole Milk"));
            db.GroceryExtras.Add(new GroceryExtra { Name = "candles" });
            await db.SaveChangesAsync();
        }
        await using (var db = As(B))
        {
            db.Products.Add(NewProduct("Coffee"));
            await db.SaveChangesAsync();
        }

        _db.HouseholdId = B;
        var service = new UserDataService(_db, new FakeCurrentHousehold(), null, NullLogger<UserDataService>.Instance);

        var export = await service.ExportAsync();
        Assert.Equal("Coffee", Assert.Single(export.Products).Name);
        Assert.Empty(export.GroceryExtras);

        await service.DeleteAllAsync();

        await using var raw = _db.CreateUnscopedContext();
        // Pins that ExecuteDelete composes over the query filter: A's pantry survives B's wipe.
        var survivors = await raw.Products.IgnoreQueryFilters().ToListAsync();
        Assert.Equal("Whole Milk", Assert.Single(survivors).Name);
        Assert.Equal(A, survivors[0].HouseholdId);
        Assert.Single(await raw.GroceryExtras.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_household_can_seed_demo_data_while_another_has_a_pantry()
    {
        await using (var db = As(A))
        {
            db.Products.Add(NewProduct("Whole Milk"));
            await db.SaveChangesAsync();
        }

        _db.HouseholdId = B;
        var seeded = await new DemoDataSeeder(_db).SeedAsync();
        Assert.True(seeded.Seeded);

        // …and the guard still refuses a SECOND seed for the same household.
        var again = await new DemoDataSeeder(_db).SeedAsync();
        Assert.False(again.Seeded);

        await using (var db = As(A))
        {
            Assert.Equal("Whole Milk", Assert.Single(await db.Products.ToListAsync()).Name);
        }
        await using (var db = As(B))
        {
            Assert.True(await db.Products.CountAsync() > 10);
        }
    }
}
