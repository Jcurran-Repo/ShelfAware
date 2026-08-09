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
    public async Task A_rename_cannot_create_a_name_the_MATCHER_cannot_tell_apart()
    {
        // ⚠️ "Taken" is rule-1 identity, not raw equality: renaming to "Half and Half" beside an
        // existing "Half-and-Half" made two products the matcher treats as one — splitting the item's
        // history, and jamming every later shelf census of it with an AmbiguousName refusal escapable
        // only by picking from the dropdown (another vision call). This is the third guard that was
        // still comparing raw names after the census moved to one identity rule; the browser pass
        // built its twin fixture through exactly this hole.
        await SeedProduct("Half-and-Half");
        var otherId = await SeedProduct("Oat Milk");

        var result = await _service.RenameAsync(otherId, "Half and Half");

        Assert.False(result.Ok);
        Assert.Contains("already exists", result.Message);
        await using var db = _db.CreateDbContext();
        Assert.Equal("Oat Milk", (await db.Products.SingleAsync(p => p.Id == otherId)).Name);
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
    public async Task Relinks_a_recipe_ingredient_whose_MatchedProduct_differs_only_in_PUNCTUATION()
    {
        // ⚠️ The recipe re-point is by rule-1 identity, not ToLower(): a MatchedProduct stored as
        // "Home Canned Sauce" for a product named "Home-Canned Sauce" is the same product to every other
        // guard, so a rename that skipped it left "recipes that use this" and makeability silently stale.
        var id = await SeedProduct("Home-Canned Sauce");
        await using (var db = _db.CreateDbContext())
        {
            db.Recipes.Add(new Recipe
            {
                Name = "Pasta",
                Ingredients = [new RecipeIngredient { Name = "Sauce", IsMain = true, MatchedProduct = "Home Canned Sauce" }],
            });
            await db.SaveChangesAsync();
        }

        var result = await _service.RenameAsync(id, "Grandma's Canned Sauce");

        Assert.True(result.Ok);
        Assert.Equal(1, result.RelinkedIngredients);
        await using var read = _db.CreateDbContext();
        Assert.Equal("Grandma's Canned Sauce", (await read.RecipeIngredients.SingleAsync()).MatchedProduct);
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
