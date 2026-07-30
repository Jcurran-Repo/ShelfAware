using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Reporting;
using ShelfAware.Core.Shopping;
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

        // Everything BOUGHT must be priced. Census stock is the deliberate exception and states itself:
        // it has no purchases because no receipt ever saw it (§13.8), so it can have no priced line
        // either. Asserted as an exact set rather than skipped, so a product that loses its receipt lines
        // by accident still fails this.
        var bought = read.Products.Include(p => p.Purchases).ToList();
        Assert.All(bought.Where(p => p.Purchases.Count > 0), p => Assert.Contains(p.Id, pricedProducts));
        Assert.Equal(
            ["Quarter Cow Ground Beef"],
            bought.Where(p => p.Purchases.Count == 0).Select(p => p.Name).OrderBy(n => n).ToArray());

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

    /// <summary>The seeded catalog, loaded the way a page loads it.</summary>
    private static async Task<Product> SeededProduct(TestDb db, string name)
    {
        await using var read = db.CreateDbContext();
        return await read.Products
            .AsNoTracking()
            .Include(p => p.Purchases)
            .Include(p => p.Signals)
            .SingleAsync(p => p.Name == name);
    }

    [Fact]
    public async Task Seeds_a_stale_count_so_the_drift_check_has_something_to_catch()
    {
        // §13.5's drift check is the one behaviour NO UI path can produce: every write stamps the
        // attestation as NOW, so without a seed it could only be seen by waiting three months. This is
        // the demo hero that makes it visible — and the test that proves the engine really does stand its
        // suppression down rather than trusting a count forever.
        using var db = new TestDb();
        await new DemoDataSeeder(db).SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var tomatoes = await SeededProduct(db, "Canned Diced Tomatoes");
        Assert.True(tomatoes.TrackQuantity);
        Assert.Equal(3m, tomatoes.QuantityOnHand);

        var r = ReplenishmentPredictor.Predict(tomatoes, today, honorQuantity: true);

        Assert.True(r.CountLooksStale);
        Assert.False(r.SuppressedByCount); // the count no longer holds the recommendation back
        Assert.NotNull(r.CountRunsOutOn);
        Assert.True(r.CountRunsOutOn < today,
            $"the count should have run out before today, got {r.CountRunsOutOn}");
        // And it's genuinely back on the buy list, which is what the product page now claims only when true.
        Assert.True(r.Status is PredictionStatus.Overdue or PredictionStatus.DueSoon, $"got {r.Status}");
    }

    [Fact]
    public async Task Seeds_a_weight_item_so_one_package_is_a_real_pack_not_a_round_one()
    {
        // The branch that had no real-world instance: 0 of 537 purchases on the real dev database carry a
        // fractional quantity, so until this hero existed §13.3's median path was only ever exercised by
        // hand-built unit tests. Now the seeded catalog contains the shape extraction writes for a
        // weight-priced line, and the whole chain is checked on it.
        using var db = new TestDb();
        await new DemoDataSeeder(db).SeedAsync();

        var chuck = await SeededProduct(db, "Ground Chuck");
        Assert.Contains(chuck.Purchases, p => p.Quantity != decimal.Truncate(p.Quantity));

        // One package is the household's own pack (median of 1.18/1.22/1.24/1.31), NOT 1.
        var onePackage = TypicalPackage.Of(chuck.Purchases.Select(p => p.Quantity));
        Assert.Equal(1.23m, onePackage);
        // Driven by the FRACTIONALITY, not by the unit — clearing the label must not change the amount.
        Assert.Equal(onePackage, TypicalPackage.Of(chuck.Purchases.Select(p => p.Quantity)));
        // …and the unit is what makes the display honest rather than a bare number.
        Assert.Equal("lb", chuck.DefaultUnit);
        Assert.Equal("3.72 lb", QuantityFormat.Describe(chuck.QuantityOnHand!.Value, chuck.DefaultUnit));
    }

    [Fact]
    public async Task Seeds_a_counted_item_with_a_label_so_the_label_can_be_seen_to_win()
    {
        // §13.5's sharpest interaction, and the other thing I could not verify live (it needs the
        // household toggle flipped). Same product, both ways, through the real seeded data.
        using var db = new TestDb();
        await new DemoDataSeeder(db).SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var milk = await SeededProduct(db, "Heavy Whipping Cream");
        Assert.Equal(2m, milk.QuantityOnHand);
        var label = milk.Purchases.Max(p => p.ExpirationDate);
        Assert.NotNull(label); // the catalog's one dated purchase — Waste watch needs it too

        // Toggle OFF: dormant, exactly as v3.6 ships. The count does its normal job.
        var blind = ReplenishmentPredictor.Predict(milk, today, honorQuantity: true);
        Assert.True(blind.SuppressedByCount);
        Assert.Equal(PredictionStatus.Stocked, blind.Status);

        // Toggle ON: the label takes over. A count says how many, never whether they're still good — so
        // suppression stands down and the item reaches Due Soon BEFORE it dies instead of after.
        var honoured = ReplenishmentPredictor.Predict(milk, today, honorExpirations: true, honorQuantity: true);
        Assert.False(honoured.SuppressedByCount);
        Assert.True(honoured.DueCappedByExpiration);
        Assert.Equal(label, honoured.DueDate);
        Assert.Equal(PredictionStatus.DueSoon, honoured.Status);
    }

    [Fact]
    public async Task Seeds_census_stock_whose_count_is_the_only_thing_that_makes_it_usable()
    {
        // §13.8's output shape, seeded now so the behaviour it depends on is demonstrable before the census
        // itself is built: a product with a count and NO purchase history. Its status stays Unknown
        // forever — correct, since the app was never going to ask you to buy it — so the count reaching
        // recipes is the entire value, and reading status alone left it as decoration.
        using var db = new TestDb();
        await new DemoDataSeeder(db).SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var beef = await SeededProduct(db, "Quarter Cow Ground Beef");
        Assert.Empty(beef.Purchases);
        Assert.Equal(14m, beef.QuantityOnHand);

        var r = ReplenishmentPredictor.Predict(beef, today, honorQuantity: true);
        Assert.Equal(PredictionStatus.Unknown, r.Status); // nothing to suppress, and that's the design
        Assert.False(r.SuppressedByCount);
        Assert.False(r.CountLooksStale); // counted 20 days ago — inside the 90-day age window
        Assert.Null(r.CountRunsOutOn); // no rhythm to project from, so no invented date

        // The payoff: recipes can see it, and can see it go.
        Assert.Contains(beef, PantryOnHand.EdibleInStock([beef], today));
        beef.QuantityOnHand = 0m;
        Assert.Empty(PantryOnHand.EdibleInStock([beef], today));
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
