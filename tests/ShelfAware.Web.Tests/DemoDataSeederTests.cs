using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

public class DemoDataSeederTests
{
    [Fact]
    public async Task Seeds_an_empty_catalog_with_a_lively_mix()
    {
        using var db = new TestDb();

        var result = await new DemoDataSeeder(db).SeedAsync();

        Assert.True(result.Seeded);
        Assert.True(result.Products >= 30, $"expected a substantial catalog, got {result.Products}");

        await using var read = db.CreateDbContext();
        Assert.Equal(result.Products, read.Products.Count());
        Assert.True(read.PurchaseEvents.Any());
        Assert.True(read.Recipes.Any(r => r.ParentRecipeId != null)); // the adapted variant grouped under its parent
        Assert.True(read.ExcludedFoods.Any());
    }

    [Fact]
    public async Task Skips_when_the_catalog_already_has_products()
    {
        using var db = new TestDb();
        var seeder = new DemoDataSeeder(db);
        await seeder.SeedAsync();

        var again = await seeder.SeedAsync(); // the guard: it must never clobber existing (real) data

        Assert.False(again.Seeded);
        Assert.Equal(0, again.Products);
    }
}
