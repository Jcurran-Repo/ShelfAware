using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
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
    public async Task Seeds_confirmed_receipt_prices_for_every_product()
    {
        using var db = new TestDb();
        await new DemoDataSeeder(db).SeedAsync();

        await using var read = db.CreateDbContext();
        // Cost surfaces (grocery-list estimates, Trends, price history) price from confirmed receipt
        // lines — a catalog of bare purchases renders $0 everywhere, which is the bug this pins.
        Assert.All(read.Receipts.ToList(), r => Assert.Equal(ReceiptStatus.Confirmed, r.Status)); // hidden from Upload's review queue
        var pricedProducts = read.ReceiptLines
            .Where(l => l.UnitPrice > 0 && l.ProductId != null)
            .Select(l => l.ProductId!.Value).Distinct().ToHashSet();
        Assert.All(read.Products.ToList(), p => Assert.Contains(p.Id, pricedProducts));

        // Each buy ties to a same-day trip receipt so per-purchase price lookups (Trends spend) hit exactly.
        Assert.All(read.PurchaseEvents.Include(pe => pe.Receipt).ToList(), pe =>
        {
            Assert.NotNull(pe.Receipt);
            Assert.Equal(pe.PurchasedAt, pe.Receipt!.PurchasedAt);
        });
    }

    [Fact]
    public async Task Seeds_a_climbing_price_hero_so_trends_has_a_story()
    {
        using var db = new TestDb();
        await new DemoDataSeeder(db).SeedAsync();

        await using var read = db.CreateDbContext();
        var coffee = read.Products.Single(p => p.Name == "Ground Coffee");
        var prices = read.ReceiptLines
            .Include(l => l.Receipt)
            .Where(l => l.ProductId == coffee.Id)
            .AsEnumerable() // order client-side; DateOnly ordering isn't worth a translation dependency
            .OrderBy(l => l.Receipt!.PurchasedAt)
            .Select(l => l.UnitPrice!.Value)
            .ToList();
        Assert.True(prices.Count >= 2, "expected several coffee buys");
        Assert.True(prices[^1] > prices[0] * 1.05m,
            $"expected a clear price climb for the Trends ticker, got {prices[0]} → {prices[^1]}");
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
