using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

public class ProductRenameServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ProductRenameService _service;

    public ProductRenameServiceTests() => _service = new ProductRenameService(_db);

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedProduct(string name)
    {
        await using var db = _db.CreateDbContext();
        var product = new Product { Name = name };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    [Fact]
    public async Task Renames_and_relinks_matched_recipe_ingredients()
    {
        // RecipeIngredient.MatchedProduct is a name string grounded at save time — it drives
        // "recipes that use this", the ?uses filter, and makeability, so a rename must re-point it.
        var beefId = await SeedProduct("Ground Beef");
        await SeedProduct("Chicken Breast");
        await using (var db = _db.CreateDbContext())
        {
            db.Recipes.Add(new Recipe
            {
                Name = "Tacos",
                Ingredients =
                [
                    new RecipeIngredient { Name = "Ground beef", IsMain = true, MatchedProduct = "ground beef" }, // case differs
                    new RecipeIngredient { Name = "Chicken", IsMain = true, MatchedProduct = "Chicken Breast" },  // other product — untouched
                ],
            });
            await db.SaveChangesAsync();
        }

        var result = await _service.RenameAsync(beefId, "Wagyu Ground Beef");

        Assert.True(result.Ok);
        Assert.Equal(1, result.RelinkedIngredients);
        await using var read = _db.CreateDbContext();
        Assert.Equal("Wagyu Ground Beef", (await read.Products.SingleAsync(p => p.Id == beefId)).Name);
        var ingredients = await read.RecipeIngredients.OrderBy(i => i.Name).ToListAsync();
        Assert.Equal("Chicken Breast", ingredients.Single(i => i.Name == "Chicken").MatchedProduct);
        Assert.Equal("Wagyu Ground Beef", ingredients.Single(i => i.Name == "Ground beef").MatchedProduct);
    }

    [Fact]
    public async Task Rejects_a_name_another_product_already_uses()
    {
        var beefId = await SeedProduct("Ground Beef");
        await SeedProduct("Chicken Breast");

        var result = await _service.RenameAsync(beefId, "chicken breast"); // case-insensitive collision

        Assert.False(result.Ok);
        Assert.Contains("already exists", result.Message);
        await using var read = _db.CreateDbContext();
        Assert.Equal("Ground Beef", (await read.Products.SingleAsync(p => p.Id == beefId)).Name);
    }

    [Fact]
    public async Task Allows_a_case_only_fix_of_the_same_product()
    {
        var id = await SeedProduct("wagyu beef tips");

        var result = await _service.RenameAsync(id, "Wagyu Beef Tips");

        Assert.True(result.Ok);
        await using var read = _db.CreateDbContext();
        Assert.Equal("Wagyu Beef Tips", (await read.Products.SingleAsync(p => p.Id == id)).Name);
    }

    [Fact]
    public async Task Rejects_blank_names_and_missing_products()
    {
        var id = await SeedProduct("Ground Beef");

        Assert.False((await _service.RenameAsync(id, "   ")).Ok);
        Assert.False((await _service.RenameAsync(99999, "Anything")).Ok);
    }
}
