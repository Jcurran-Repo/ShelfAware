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
        // sample data showing the thing it exists for — three months of beef in the freezer. This runs the
        // seeded rows through the REAL load the page uses, so it asserts the DATA reads as a hoard, not
        // merely that the rows exist.
        using var db = new TestDb();
        await new DemoDataSeeder(db).SeedAsync();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var service = new ReportDataService(db);
        var report = await service.LoadBacklogAsync(
            await service.LoadAsync(), await service.LoadRecipeMainsAsync(), today, honorExpirations: false, 60);

        var roast = Assert.Single(report.Findings, f => f.ProductName == "Beef Chuck Roast");
        Assert.Equal(5, roast.Trips);
        // The 6× buy stretches the projection ~84 days and the hero is still well past THAT — which is
        // what makes it a finding rather than an item merely bought in bulk.
        Assert.True(roast.OverdueDays > 30,
            $"the freezer-filling trip should have run long past due, got {roast.OverdueDays} days");
        // The deliberately missing half of the fixture: a household eating through a hoard reports nothing.
        await using var read = db.CreateDbContext();
        Assert.DoesNotContain(
            read.Products.Include(p => p.Signals).Single(p => p.Name == "Beef Chuck Roast").Signals,
            s => s.Kind == SignalKind.OutNow);
    }

    [Fact]
    public async Task Seeds_a_counted_hero_so_suppression_has_something_to_show()
    {
        // Same gap as the hoard hero above, one phase later: the backlog check NAMES items worth
        // counting, and until this seed nothing in the catalog showed what happens once you do — a
        // visitor to the demo could never see a count, a suppressed row, or the reason given for it.
        // Run through the REAL engine so it asserts the DATA behaves, not merely that the columns are set.
        using var db = new TestDb();
        await new DemoDataSeeder(db).SeedAsync();

        await using var read = db.CreateDbContext();
        var beans = read.Products
            .Include(p => p.Purchases)
            .Include(p => p.Signals)
            .Single(p => p.Name == "Canned Black Beans");

        Assert.True(beans.TrackQuantity);
        Assert.Equal(5m, beans.QuantityOnHand);

        var today = DateOnly.FromDateTime(DateTime.Today);
        // Without the count the rhythm wants it bought; with the count it stands down and says why.
        Assert.Equal(PredictionStatus.Overdue, ReplenishmentPredictor.Predict(beans, today).Status);

        var counted = ReplenishmentPredictor.Predict(beans, today, honorQuantity: true);
        Assert.Equal(PredictionStatus.Stocked, counted.Status);
        Assert.True(counted.SuppressedByCount);
        // Fresh on purpose — five packages on a ~21-day rhythm are months from the drift check, so the
        // demo reads as suppression working rather than as a count that has already rotted.
        Assert.False(counted.CountLooksStale);
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
