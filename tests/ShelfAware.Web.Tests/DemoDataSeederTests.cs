using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Reporting;
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
    public async Task Seeds_a_hoard_hero_so_the_backlog_check_has_something_to_find()
    {
        // The gap this closes: every other seeded household is well behaved, so "What's piling up" had no
        // sample data showing the thing it exists for — three months of beef in the freezer. This asserts
        // the SEED DATA really reads as a hoard once it's been through the engine, not just that the rows
        // exist. (The BacklogInput assembly mirrors Reports.razor; the page's own copy is still untested —
        // see the note in DESIGN.md §13.7.)
        using var db = new TestDb();
        await new DemoDataSeeder(db).SeedAsync();

        await using var read = db.CreateDbContext();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var products = read.Products.Include(p => p.Purchases).Include(p => p.Signals).ToList();

        var report = BacklogSignals.Find(
            products.Select(p =>
            {
                var prediction = ReplenishmentPredictor.Predict(p, today);
                return new BacklogInput(
                    p.Id,
                    p.Name,
                    p.Purchases.Select(x => x.PurchasedAt).ToList(),
                    p.Signals.Where(s => s.Kind == SignalKind.OutNow)
                        .Select(s => DateOnly.FromDateTime(s.SignaledAt.Date)).ToList(),
                    TotalQuantity: p.Purchases.Sum(x => x.Quantity),
                    PricedSpend: 0m,
                    UnpricedPurchases: 0,
                    prediction.RebuyIntervalDays,
                    prediction.DueDate,
                    RecentMealUses: 0);
            }),
            today);

        var roast = Assert.Single(report.Findings, f => f.ProductName == "Beef Chuck Roast");
        Assert.Equal(5, roast.Trips);
        // A 6× buy outruns StockUpFactor's 3× cap, so it still runs long past due — which is exactly why
        // the report is needed, and why this hero can't be replaced by a smaller stock-up.
        Assert.True(roast.OverdueDays > 30,
            $"the freezer-filling trip should have run long past due, got {roast.OverdueDays} days");
        // The deliberately missing half of the fixture: a household eating through a hoard reports nothing.
        Assert.DoesNotContain(
            products.Single(p => p.Name == "Beef Chuck Roast").Signals,
            s => s.Kind == SignalKind.OutNow);
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
