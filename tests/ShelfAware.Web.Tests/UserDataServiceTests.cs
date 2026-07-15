using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

public class UserDataServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeCurrentHousehold _household = new();
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-web-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private ReceiptStorage Storage() => new(
        new AppPaths(_dataDir, Path.Combine(_dataDir, "receipts")),
        _household,
        NullLogger<ReceiptStorage>.Instance);

    private UserDataService Service() =>
        new(_db, _household, Storage(), null, NullLogger<UserDataService>.Instance);

    // One row (or two) in every user-content table, including FK children, so a wrong delete order or a
    // missed table shows up.
    private async Task Seed()
    {
        await using var db = _db.CreateDbContext();
        var milk = new Product
        {
            Name = "Whole Milk",
            Purchases = { new PurchaseEvent { PurchasedAt = new DateOnly(2026, 7, 1) } },
            Signals = { new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = DateTimeOffset.Now } },
            Tags = { new ProductTag { Value = "Dairy" } },
            Substitutes = { new ProductSubstitute { Value = "milk" } },
        };
        db.Products.Add(milk);
        await db.SaveChangesAsync(); // assigns milk.Id so the alias FK resolves
        db.ProductAliases.Add(new ProductAlias { Merchant = "Walmart", RawText = "GV MLK", ProductId = milk.Id });
        db.Recipes.Add(new Recipe
        {
            Name = "Cereal",
            Ingredients = { new RecipeIngredient { Name = "milk", IsMain = true } },
            Steps = { new RecipeStep { Order = 1, Text = "Pour the milk" } },
        });
        db.Receipts.Add(new Receipt
        {
            ImagePath = "receipts/x",
            Lines = { new ReceiptLine { RawText = "GV MLK", NormalizedName = "Whole Milk" } },
        });
        db.ExcludedFoods.Add(new ExcludedFood { Value = "olives" });
        db.GroceryExtras.Add(new GroceryExtra { Name = "napkins" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DeleteAllAsync_empties_every_user_table()
    {
        await Seed();
        var svc = Service();
        Assert.True(await svc.CountAllAsync() > 0);

        await svc.DeleteAllAsync();

        Assert.Equal(0, await svc.CountAllAsync());
        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Products.CountAsync());
        Assert.Equal(0, await db.PurchaseEvents.CountAsync());
        Assert.Equal(0, await db.Recipes.CountAsync());
        Assert.Equal(0, await db.RecipeIngredients.CountAsync());
        Assert.Equal(0, await db.Receipts.CountAsync());
        Assert.Equal(0, await db.ReceiptLines.CountAsync());
        Assert.Equal(0, await db.GroceryExtras.CountAsync());
    }

    [Fact]
    public async Task DeleteAllAsync_on_an_empty_db_is_a_no_op()
    {
        var svc = Service();
        await svc.DeleteAllAsync(); // must not throw on nothing to delete
        Assert.Equal(0, await svc.CountAllAsync());
    }

    [Fact]
    public async Task DeleteAllAsync_removes_the_saved_receipt_images_too()
    {
        // The saved copy of a receipt is a photograph of the household's shopping. Deleting the rows and
        // leaving it on disk made "delete my data" a false statement — and because ImagePath was the only
        // pointer to the folder, the same delete put it beyond reach of any later cleanup.
        var storage = Storage();
        var imagePath = await storage.NewFolderAsync();
        await storage.WritePageAsync(imagePath, 0, [1, 2, 3], "image/jpeg");
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt { ImagePath = imagePath });
            await db.SaveChangesAsync();
        }
        Assert.True(storage.HasPages(imagePath));

        await Service().DeleteAllAsync();

        Assert.False(storage.HasPages(imagePath));
        Assert.False(Directory.Exists(Path.Combine(_dataDir, imagePath)));
    }

    [Fact]
    public async Task DeleteAllAsync_reaches_images_filed_before_receipts_were_household_scoped()
    {
        // Older rows point at "receipts/<folder>" with no household segment, so they don't live under the
        // household's tree. The stored pointer is what reaches them.
        var legacyPath = Path.Combine("receipts", "20260101-000000-legacy");
        var folder = Path.Combine(_dataDir, legacyPath);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "page-0.jpg"), [1, 2, 3]);
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt { ImagePath = legacyPath });
            await db.SaveChangesAsync();
        }

        await Service().DeleteAllAsync();

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task DeleteAllAsync_leaves_alone_a_path_outside_the_receipts_store()
    {
        // The demo seeder files rows with a placeholder ImagePath. A delete driven by stored strings must
        // not follow one out of the store and remove something that isn't a receipt.
        var outside = Path.Combine(_dataDir, "demo");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "no-image"), "not a receipt");
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt { ImagePath = "demo/no-image" });
            await db.SaveChangesAsync();
        }

        await Service().DeleteAllAsync();

        Assert.True(File.Exists(Path.Combine(outside, "no-image")));
    }

    [Fact]
    public async Task ExportAsync_returns_the_stored_content()
    {
        await Seed();
        var svc = Service();

        var export = await svc.ExportAsync();

        Assert.Single(export.Products);
        Assert.Equal("Whole Milk", export.Products[0].Name);
        Assert.Single(export.Recipes);
        Assert.Single(export.Receipts);
        Assert.Single(export.GroceryExtras);
    }
}
