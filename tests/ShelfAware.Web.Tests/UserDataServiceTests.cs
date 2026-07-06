using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

public class UserDataServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

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
        var svc = new UserDataService(_db);
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
        var svc = new UserDataService(_db);
        await svc.DeleteAllAsync(); // must not throw on nothing to delete
        Assert.Equal(0, await svc.CountAllAsync());
    }

    [Fact]
    public async Task ExportAsync_returns_the_stored_content()
    {
        await Seed();
        var svc = new UserDataService(_db);

        var export = await svc.ExportAsync();

        Assert.Single(export.Products);
        Assert.Equal("Whole Milk", export.Products[0].Name);
        Assert.Single(export.Recipes);
        Assert.Single(export.Receipts);
        Assert.Single(export.GroceryExtras);
    }
}
