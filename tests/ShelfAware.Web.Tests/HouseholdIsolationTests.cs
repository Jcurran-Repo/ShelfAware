using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Settings;
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
            var toast = new Recipe
            {
                Name = "Milk Toast",
                SavedAt = DateTimeOffset.Now,
                Ingredients = [new RecipeIngredient { Name = "milk", IsMain = true }],
                Steps = [new RecipeStep { Order = 1, Text = "Combine." }],
                Tags = [new RecipeTag { Value = "Breakfast" }],
            };
            db.Recipes.Add(toast);
            db.MealEvents.Add(new MealEvent { Recipe = toast, AteAt = new DateOnly(2026, 7, 10) });
            db.SavedReports.Add(new SavedReport { Name = "Mine", Query = "from=2026-06-01&to=2026-07-01", SavedAt = DateTimeOffset.Now });
            db.BugReports.Add(new BugReport { Body = "The milk chart looks wrong", CreatedAt = DateTimeOffset.Now });
            db.ActivityEntries.Add(new ActivityEntry
            {
                Kind = ActivityKind.PurchaseAdded, OccurredAt = DateTimeOffset.Now,
                Summary = "Bought 1 × Whole Milk", PayloadJson = "{}", Reversibility = Reversibility.Reversible,
            });
            var plan = new MealPlan { CreatedAt = DateTimeOffset.Now, StartDate = new DateOnly(2026, 9, 1), Days = 7 };
            plan.Meals.Add(new PlannedMeal { Recipe = toast, Date = new DateOnly(2026, 9, 2), Slot = MealSlot.Dinner });
            db.MealPlans.Add(plan);
            db.LookalikePairs.Add(new LookalikePair { LowerProductId = 10, HigherProductId = 20, FirstSeenAt = DateTimeOffset.Now });
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
            Assert.Empty(await db.RecipeTags.ToListAsync());
            Assert.Empty(await db.MealEvents.ToListAsync());
            Assert.Empty(await db.SavedReports.ToListAsync());
            Assert.Empty(await db.BugReports.ToListAsync());
            Assert.Empty(await db.ActivityEntries.ToListAsync());
            Assert.Empty(await db.MealPlans.ToListAsync());
            Assert.Empty(await db.PlannedMeals.ToListAsync());
            Assert.Empty(await db.LookalikePairs.ToListAsync());
        }

        await using (var db = As(A))
        {
            Assert.Single(await db.Products.Include(p => p.Purchases).ToListAsync());
            Assert.Single(await db.Recipes.Include(r => r.Ingredients).Include(r => r.Steps).ToListAsync());
            Assert.Single(await db.RecipeTags.ToListAsync());
            Assert.Single(await db.MealPlans.Include(p => p.Meals).ToListAsync());
            Assert.Single(await db.PlannedMeals.ToListAsync());
            Assert.Single(await db.LookalikePairs.ToListAsync());
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
        var store = new EfPantryStore(_db, UndoTesting.Log(_db));
        var retracked = (await store.AddPurchaseAsync(productId, new DateOnly(2026, 7, 2), 1)).Retracked;
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
        var confirmer = new ReceiptConfirmationService(_db, UndoTesting.Log(_db));
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
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.ImportMode, Value = "Review" });
            await db.SaveChangesAsync();
        }
        await using (var db = As(B))
        {
            db.Products.Add(NewProduct("Coffee"));
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.ImportMode, Value = "Auto" });
            await db.SaveChangesAsync();
        }

        _db.HouseholdId = B;
        var household = new FakeCurrentHousehold(B);
        var dataDir = Path.Combine(Path.GetTempPath(), "shelfaware-web-tests", Guid.NewGuid().ToString("N"));
        var service = new UserDataService(
            _db,
            household,
            new ReceiptStorage(
                new AppPaths(dataDir, Path.Combine(dataDir, "receipts")),
                household,
                NullLogger<ReceiptStorage>.Instance),
            new RecipeImageStorage(
                new AppPaths(dataDir, Path.Combine(dataDir, "receipts")),
                household,
                NullLogger<RecipeImageStorage>.Instance),
            null,
            new ShelfAware.Web.Auth.ApiTokenService(new TestAuthDb()),
            new ShelfAware.Web.Auth.CreditLedger(new TestAuthDb(), Microsoft.Extensions.Options.Options.Create(new ShelfAware.Core.Billing.BillingOptions())),
            NullLogger<UserDataService>.Instance);

        var export = await service.ExportAsync();
        Assert.Equal("Coffee", Assert.Single(export.Products).Name);
        Assert.Empty(export.GroceryExtras);
        Assert.Equal("Auto", Assert.Single(export.Settings).Value);

        await service.DeleteAllAsync();

        await using var raw = _db.CreateUnscopedContext();
        // Pins that ExecuteDelete composes over the query filter: A's pantry survives B's wipe.
        var survivors = await raw.Products.IgnoreQueryFilters().ToListAsync();
        Assert.Equal("Whole Milk", Assert.Single(survivors).Name);
        Assert.Equal(A, survivors[0].HouseholdId);
        Assert.Single(await raw.GroceryExtras.IgnoreQueryFilters().ToListAsync());
        // Settings are wiped by table now rather than by a list of keys, so this is the assertion that
        // the delete is still composing over the filter and not emptying AppSettings for everyone.
        var setting = Assert.Single(await raw.AppSettings.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(A, setting.HouseholdId);
        Assert.Equal("Review", setting.Value);
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
        using var seeding = new DemoSeeding(B); // storage scoped to the same household as the context
        var seeded = await seeding.Seeder(_db).SeedAsync();
        Assert.True(seeded.Seeded);

        // …and the guard still refuses a SECOND seed for the same household.
        var again = await seeding.Seeder(_db).SeedAsync();
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

    // ---- v4.0 §13: the count's own write paths take raw ids, so they walk this drill too ------------

    /// <summary>B's counted product and one of its purchases. Returns both ids so A can try to reach
    /// them the only way a caller ever could — by id.</summary>
    private async Task<(int ProductId, int PurchaseId)> CountedProductOwnedByB()
    {
        await using var db = As(B);
        var product = new Product
        {
            Name = "Beef Chuck Roast",
            Category = Category.Meat,
            TrackQuantity = true,
            QuantityOnHand = 6m,
            QuantityCountedAt = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero),
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        var purchase = new PurchaseEvent
        {
            ProductId = product.Id,
            PurchasedAt = new DateOnly(2026, 7, 1),
            Quantity = 6m,
            Source = PurchaseSource.Receipt,
        };
        db.PurchaseEvents.Add(purchase);
        await db.SaveChangesAsync();
        return (product.Id, purchase.Id);
    }

    private async Task<Product> ReadAsB(int productId)
    {
        await using var db = As(B);
        return await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
    }

    [Fact]
    public async Task Setting_a_count_cannot_reach_another_households_product()
    {
        // SetQuantityAsync resolves a RAW product id through FindAsync, and the whole write rests on the
        // claim that FindAsync applies the global query filter. That claim was probed once for other
        // paths; these are new paths, so it gets proved here rather than inherited.
        var (productId, _) = await CountedProductOwnedByB();

        _db.HouseholdId = A;
        var store = new EfPantryStore(_db, UndoTesting.Log(_db));

        Assert.False(await store.SetQuantityAsync(productId, 99));
        Assert.False(await store.SetQuantityAsync(productId, -1, relative: true));
        Assert.False(await store.SetQuantityAsync(productId, 0, stopCounting: true));

        var untouched = await ReadAsB(productId);
        Assert.True(untouched.TrackQuantity);
        Assert.Equal(6m, untouched.QuantityOnHand);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero), untouched.QuantityCountedAt);
        // And no phantom outage was filed against B by A's asserted zero.
        await using var raw = _db.CreateUnscopedContext();
        Assert.Empty(await raw.InventorySignals.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Setting_a_unit_cannot_reach_another_households_product()
    {
        // Same shape as the count: a raw product id through the filtered FindAsync. New write path,
        // so it walks the same drill rather than inheriting the claim.
        var (productId, _) = await CountedProductOwnedByB();

        _db.HouseholdId = A;
        Assert.False(await new EfPantryStore(_db, UndoTesting.Log(_db)).SetDefaultUnitAsync(productId, "lb"));

        Assert.Null((await ReadAsB(productId)).DefaultUnit);
    }

    [Fact]
    public async Task Correcting_a_purchase_cannot_reach_another_households_purchase()
    {
        var (productId, purchaseId) = await CountedProductOwnedByB();

        _db.HouseholdId = A;
        Assert.False(await new EfPantryStore(_db, UndoTesting.Log(_db)).SetPurchaseQuantityAsync(purchaseId, 1));

        await using var raw = _db.CreateUnscopedContext();
        Assert.Equal(6m, // the history A tried to rewrite
            (await raw.PurchaseEvents.IgnoreQueryFilters().SingleAsync(p => p.Id == purchaseId)).Quantity);
        Assert.Equal(6m, (await ReadAsB(productId)).QuantityOnHand); // …and the shelf it would have moved
    }

    [Fact]
    public async Task Cooking_cannot_decrement_a_same_named_product_in_another_household()
    {
        // MealStock matches counted products by NAME, and two households naturally hold the same names.
        // A's recipe names "Beef Chuck Roast"; only B counts one. A cooking it must find nothing.
        var (productId, _) = await CountedProductOwnedByB();

        int recipeId;
        await using (var db = As(A))
        {
            var recipe = new Recipe
            {
                Name = "A's Roast Dinner",
                Ingredients = [new RecipeIngredient { Name = "roast", IsMain = true, MatchedProduct = "Beef Chuck Roast" }],
            };
            db.Recipes.Add(recipe);
            await db.SaveChangesAsync();
            recipeId = recipe.Id;
        }

        await using (var db = As(A))
        {
            var recipe = await db.Recipes.Include(r => r.Ingredients).SingleAsync(r => r.Id == recipeId);
            var resolution = await MealStock.ResolveAsync(db, recipe);
            Assert.Empty(resolution.Ambiguous); // B's product isn't even a candidate to be ambiguous about
            Assert.Empty(MealStock.Apply(resolution));
            await db.SaveChangesAsync();
        }

        Assert.Equal(6m, (await ReadAsB(productId)).QuantityOnHand);
    }
}
