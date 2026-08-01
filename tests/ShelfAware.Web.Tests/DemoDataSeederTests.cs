using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Reporting;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Shopping;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

public class DemoDataSeederTests : IDisposable
{
    private readonly DemoSeeding _seeding = new();

    public void Dispose() => _seeding.Dispose();

    [Fact]
    public async Task Seeds_an_empty_catalog_with_a_lively_mix()
    {
        using var db = new TestDb();

        var result = await _seeding.Seeder(db).SeedAsync();

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
        await _seeding.Seeder(db).SeedAsync();

        await using var read = db.CreateDbContext();
        // Cost surfaces (grocery-list estimates, Trends, price history) price from confirmed receipt
        // lines — a catalog of bare purchases renders $0 everywhere, which is the bug this pins.
        // Every TRIP is confirmed (and so hidden from Upload's review queue); the one deliberate
        // exception is the sample receipt that is meant to be sitting there waiting to be reviewed.
        var pending = Assert.Single(read.Receipts.Where(r => r.Status == ReceiptStatus.PendingReview).ToList());
        Assert.Equal("Walmart Supercenter", pending.Merchant);
        Assert.All(read.Receipts.Where(r => r.Id != pending.Id).ToList(),
            r => Assert.Equal(ReceiptStatus.Confirmed, r.Status));
        var pricedProducts = read.ReceiptLines
            .Where(l => l.ReceiptId != pending.Id) // nothing is priced FROM a receipt nobody has confirmed
            .Where(l => l.UnitPrice > 0 && l.ProductId != null)
            .Select(l => l.ProductId!.Value).Distinct().ToHashSet();

        // Everything BOUGHT must be priced. Census stock is the deliberate exception and states itself:
        // it has no purchases because no receipt ever saw it (§13.8), so it can have no priced line
        // either. Asserted as an exact set rather than skipped, so a product that loses its receipt lines
        // by accident still fails this.
        var bought = read.Products.Include(p => p.Purchases).ToList();
        Assert.All(bought.Where(p => p.Purchases.Count > 0), p => Assert.Contains(p.Id, pricedProducts));
        Assert.Equal(
            ["Home-Canned Tomato Sauce", "Quarter Cow Ground Beef"],
            bought.Where(p => p.Purchases.Count == 0).Select(p => p.Name).OrderBy(n => n).ToArray());

        // Each buy FROM A RECEIPT ties to its same-day trip, so per-purchase price lookups (Trends
        // spend) hit exactly. Manual and chat buys deliberately carry no receipt at all — that's what
        // makes them a different case for removal — so they're excluded rather than the rule loosened.
        var fromReceipts = read.PurchaseEvents.Include(pe => pe.Receipt)
            .Where(pe => pe.Source == PurchaseSource.Receipt).ToList();
        Assert.All(fromReceipts, pe =>
        {
            Assert.NotNull(pe.Receipt);
            Assert.Equal(pe.PurchasedAt, pe.Receipt!.PurchasedAt);
        });
        Assert.All(read.PurchaseEvents.Where(pe => pe.Source != PurchaseSource.Receipt).ToList(),
            pe => Assert.Null(pe.ReceiptId));
    }

    [Fact]
    public async Task Seeds_a_climbing_price_hero_so_trends_has_a_story()
    {
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();

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
        await _seeding.Seeder(db).SeedAsync();

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
        await _seeding.Seeder(db).SeedAsync();

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
        await _seeding.Seeder(db).SeedAsync();
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
        await _seeding.Seeder(db).SeedAsync();

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
        await _seeding.Seeder(db).SeedAsync();
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
        await _seeding.Seeder(db).SeedAsync();
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
    public async Task Seeds_an_aging_census_count_so_the_confidence_band_is_visible()
    {
        // The pair to the Quarter Cow: identical shape, opposite confidence. Counted 140 days ago with no
        // rhythm, so there is no exhaustion date to be past and the app can only say it has stopped
        // believing the number — which is why the page attributes it to its date instead of restating it.
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var sauce = await SeededProduct(db, "Home-Canned Tomato Sauce");
        Assert.Empty(sauce.Purchases);

        var r = ReplenishmentPredictor.Predict(sauce, today, honorQuantity: true);

        Assert.Equal(CountConfidence.Aging, r.CountConfidence);
        Assert.True(r.CountLooksStale);
        Assert.Null(r.CountRunsOutOn); // no rate, so no depth claim is available or invented
        // A count nobody has vouched for in months stops deciding recipe stock and defers to the rhythm.
        Assert.Contains(sauce, PantryOnHand.EdibleInStock([sauce], today));

        // …and the believed one still reads as believed, so the demo shows both bands side by side.
        var beef = await SeededProduct(db, "Quarter Cow Ground Beef");
        Assert.Equal(CountConfidence.Counted,
            ReplenishmentPredictor.Predict(beef, today, honorQuantity: true).CountConfidence);
    }

    [Fact]
    public async Task Seeds_a_restock_that_clears_an_outage_without_teaching_the_rhythm()
    {
        // Restocked was the one SignalKind with no instance anywhere in the catalog, which left three
        // behaviours undemonstrated at once: an out being cleared by something other than a purchase,
        // a due date re-anchored to a stock-back, and (below, on the bacon) a human overriding a label.
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var paper = await SeededProduct(db, "Toilet Paper");
        Assert.Contains(paper.Signals, s => s.Kind == SignalKind.OutNow);
        var restock = Assert.Single(paper.Signals, s => s.Kind == SignalKind.Restocked);

        var r = ReplenishmentPredictor.Predict(paper, today);

        // The out is over — a stock-back later than the outage retires it, so nothing is pinned.
        Assert.False(r.Pinned);
        Assert.Null(r.SignalNote);
        // …and the projection now runs from the restock, not from the last purchase.
        Assert.Equal(SignalDate.Of(restock.SignaledAt).AddDays((int)r.MedianIntervalDays!.Value), r.DueDate);
        // But it is status-only: a restock is not a purchase and must never move a learned rhythm.
        var buysOnly = paper.Purchases.Select(p => p.PurchasedAt).OrderBy(d => d).ToList();
        var gaps = buysOnly.Zip(buysOnly.Skip(1), (a, b) => b.DayNumber - a.DayNumber).ToList();
        Assert.Contains(r.RebuyIntervalDays!.Value, gaps.Select(g => (double)g));
    }

    [Fact]
    public async Task Seeds_a_passed_label_and_the_human_overriding_one()
    {
        // Two of the four expiration flags had no instance: nothing was ever EXPIRED (the catalog's only
        // label was in the future) and nothing had been OVERRIDDEN (which needs a Restocked, which didn't
        // exist). Both are seeded now, on one product each, and asserted through the engine both ways.
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var spinach = await SeededProduct(db, "Baby Spinach");
        var expired = ReplenishmentPredictor.Predict(spinach, today, honorExpirations: true);
        Assert.True(expired.Expired);
        Assert.True(expired.Pinned);
        Assert.Equal(PredictionStatus.Overdue, expired.Status);
        Assert.Equal(expired.ExpiresOn, expired.DueDate); // "due" is the day it went bad, like an outage
        // …and it is DORMANT with the toggle off, which is the half of v3.6 that is easy to break.
        Assert.False(ReplenishmentPredictor.Predict(spinach, today).Expired);

        var bacon = await SeededProduct(db, "Bacon");
        var overridden = ReplenishmentPredictor.Predict(bacon, today, honorExpirations: true);
        Assert.True(overridden.ExpirationOverridden);
        Assert.False(overridden.Expired);          // the human said it's fine
        Assert.False(overridden.DueCappedByExpiration); // and the cap stands down too — half an override would lie
        Assert.True(bacon.Purchases.Max(p => p.ExpirationDate) < today); // the label really has passed
    }

    [Fact]
    public async Task Seeds_every_verdict_waste_watch_can_reach()
    {
        // The panel judges a dated purchase five ways and the catalog could previously produce exactly
        // one of them — "not due yet" — so its headline list was always empty and the four verdicts that
        // read evidence were dead code as far as any visitor could tell.
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var reports = new ReportDataService(db);

        var outcomes = await reports.LoadLabelOutcomesAsync(await reports.LoadAsync(), today);

        Assert.Empty(Enum.GetValues<LabelOutcome>().Except(outcomes.Select(o => o.Outcome)));
        // The one that matters most is the one that costs money: it must name a product and a price,
        // since "worth checking, $ at stake" is the only claim this panel is allowed to make.
        var quiet = Assert.Single(outcomes, o => o.Outcome == LabelOutcome.PassedQuietly);
        Assert.Equal("Baby Spinach", quiet.Purchase.ProductName);
        Assert.True(quiet.Purchase.Price > 0);
    }

    [Fact]
    public async Task Seeds_both_branches_of_the_mixed_size_rule()
    {
        // "Milk as a gallon or a half-gallon" is the case the whole size-is-metadata decision exists for,
        // and no seeded product was ever bought in two sizes — so both branches of the rule went unshown.
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        // Branch 1: one size bought ≥2 times drives the cadence alone, and is what gets recommended.
        var juice = await SeededProduct(db, "Orange Juice");
        var dominant = ReplenishmentPredictor.Predict(juice, today);
        Assert.True(juice.Purchases.Select(p => SizeBucket.Key(p.Size)).Distinct().Count() > 1);
        Assert.Equal("52 oz", dominant.RecommendedSize);
        var dominantGaps = juice.Purchases.Where(p => p.Size == "52 oz").Select(p => p.PurchasedAt)
            .OrderBy(d => d).ToList();
        Assert.Equal(
            Median(dominantGaps.Zip(dominantGaps.Skip(1), (a, b) => b.DayNumber - a.DayNumber)),
            dominant.RebuyIntervalDays);

        // Branch 2: no size bought twice, so it falls back to ALL purchases rather than learning a
        // rhythm from a single bucket's one date — a mixed-size item still predicts.
        var pb = await SeededProduct(db, "Peanut Butter");
        Assert.Equal(pb.Purchases.Count, pb.Purchases.Select(p => SizeBucket.Key(p.Size)).Distinct().Count());
        var allGaps = pb.Purchases.Select(p => p.PurchasedAt).OrderBy(d => d).ToList();
        Assert.Equal(
            Median(allGaps.Zip(allGaps.Skip(1), (a, b) => b.DayNumber - a.DayNumber)),
            ReplenishmentPredictor.Predict(pb, today).RebuyIntervalDays);
    }

    private static double Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    [Fact]
    public async Task Seeds_a_counted_recipe_main_so_cooking_actually_moves_a_count()
    {
        // Measured before this seed: "Ate it" took NOTHING for every recipe in the catalog, because no
        // counted product was any main's grounded match. The flagship v4.1 flow — tap, "took one off X",
        // Undo — reported "nothing to take" and there was no way to tell that from a bug.
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        await using var read = db.CreateDbContext();

        var recipe = read.Recipes.Include(r => r.Ingredients).First(r => r.Name == "Weeknight Chicken & Rice");
        var resolution = await MealStock.ResolveAsync(read, recipe);

        var rice = Assert.Single(resolution.Products);
        Assert.Equal("White Rice", rice.Name);
        Assert.Empty(resolution.Ambiguous); // grounded to a counted product, so nothing to ask about

        var before = rice.QuantityOnHand!.Value;
        var applied = Assert.Single(MealStock.Apply(resolution));
        Assert.Equal(1m, applied.Taken);
        Assert.Equal(before - 1m, applied.Remaining);

        // …and the picker still has its own case, on the other recipe: a grounded link to a product that
        // exists but ISN'T counted must ask which one came off the shelf rather than guess.
        var tacos = read.Recipes.Include(r => r.Ingredients).First(r => r.Name == "Skillet Beef Tacos");
        var asked = Assert.Single((await MealStock.ResolveAsync(read, tacos)).Ambiguous);
        Assert.Equal("Ground beef", asked.Ingredient);
        Assert.Contains(asked.Candidates, c => c.ProductName == "Quarter Cow Ground Beef");
    }

    [Fact]
    public async Task Seeds_the_product_states_a_well_behaved_catalog_never_reaches()
    {
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        // Counting turned back OFF, number kept (v4.1's "dormant, not destructive"). The pair has to
        // survive AND influence nothing — a reader that forgot to check the flag would fail here.
        var litter = await SeededProduct(db, "Cat Litter");
        Assert.False(litter.TrackQuantity);
        Assert.NotNull(litter.QuantityOnHand);
        Assert.NotNull(litter.QuantityCountedAt);
        var dormant = ReplenishmentPredictor.Predict(litter, today, honorQuantity: true);
        Assert.Equal(CountConfidence.NotCounted, dormant.CountConfidence);
        Assert.False(dormant.SuppressedByCount);

        // Stopped tracking: gone from every buying surface, still in the catalog under the grid's filter.
        var cider = await SeededProduct(db, "Sparkling Cider");
        Assert.False(cider.IsTracked);

        // A pre-variety split product — the merge panel's only candidate. It still carries its flavor in
        // its NAME, which is exactly the shape ⇆ Merge exists to fold back into the parent item.
        await using var read = db.CreateDbContext();
        Assert.True(read.Products.Any(p => p.Name == "Strawberry Drink Mix"));
        Assert.True(read.Products.Any(p => p.Name == "Drink Mix"));
        // All ten aisles, so the grocery list's ordering is exercised end to end.
        Assert.Empty(Enum.GetValues<Category>().Except(read.Products.Select(p => p.Category).Distinct()));
    }

    [Fact]
    public async Task Seeds_a_receipt_waiting_to_be_reviewed_with_the_image_it_was_read_from()
    {
        // The review grid is the app's most involved screen and it was reachable only by uploading a
        // real receipt with a working key. These lines are already extracted, so it reviews and confirms
        // with no key at all.
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        await using var read = db.CreateDbContext();

        var pending = Assert.Single(read.Receipts.Include(r => r.Lines)
            .Where(r => r.Status == ReceiptStatus.PendingReview).ToList());

        // The audit copy is really on disk, which is what "Retry" re-reads and what /receipts links to.
        Assert.True(_seeding.Storage.HasPages(pending.ImagePath));

        // The lines are the contents of that image. If these drift, the screen describes one receipt
        // and shows another — so a couple of them are pinned literally, including the weight-priced
        // line (quantity is the WEIGHT, unit goes in size) and the low-confidence row that is the only
        // one in the catalog and the only reason the grid's low-confidence styling is ever seen.
        Assert.Equal(6, pending.Lines.Count);
        var bananas = Assert.Single(pending.Lines, l => l.NormalizedName == "Bananas");
        Assert.Equal(2.31m, bananas.Quantity);
        Assert.Equal("lb", bananas.Size);
        Assert.True(bananas.Confidence < 0.6m);
        Assert.Contains(pending.Lines, l => l.RawText == "GV WHL MLK 1GAL" && l.Brand == "Great Value");

        // Every line carries the model's own product suggestion and its tags through the queue — the
        // second rung of the review screen's trust order, and the tag chips the editor starts from.
        Assert.All(pending.Lines, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.SuggestedProduct));
            Assert.False(string.IsNullOrWhiteSpace(l.TagsJson));
        });
        Assert.NotEqual("", pending.RawModelJson); // kept for audit, like any other import
    }

    [Fact]
    public async Task Seeds_learned_aliases_that_name_the_confirm_that_taught_them()
    {
        // An alias is the FIRST thing an upload consults, ahead of the model's suggestion and ahead of
        // fuzzy matching. With none seeded, the trust order could only ever be seen from its second rung.
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        await using var read = db.CreateDbContext();

        var aliases = read.ProductAliases.ToList();
        Assert.NotEmpty(aliases);
        var productIds = read.Products.Select(p => p.Id).ToHashSet();
        var confirmed = read.Receipts.Where(r => r.Status == ReceiptStatus.Confirmed)
            .Select(r => r.Id).ToHashSet();
        Assert.All(aliases, a =>
        {
            Assert.Contains(a.ProductId, productIds);
            // Only the teacher's removal un-teaches a pairing, so an alias with no teacher is a pairing
            // that can never be undone.
            Assert.Contains(a.TaughtByReceiptId!.Value, confirmed);
        });

        // …and deliberately NONE of them matches the receipt waiting in the queue: it's the household's
        // first from that merchant, so the pre-fill falls through to the model's suggestion and
        // confirming it is what teaches the aliases — visibly.
        var pending = read.Receipts.Include(r => r.Lines).Single(r => r.Status == ReceiptStatus.PendingReview);
        Assert.DoesNotContain(aliases, a => a.Merchant == pending.Merchant);
    }

    [Fact]
    public async Task Seeds_saved_reports_the_engine_will_actually_run()
    {
        // A saved report is a spec string, and a spec that breaks the chart-honesty rules is REFUSED by
        // the engine — so a seeded row that looked fine could greet a visitor with an error. Parse them
        // back through the real parser and put them to the real rules.
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        await using var read = db.CreateDbContext();
        var saved = read.SavedReports.ToList();
        Assert.NotEmpty(saved);
        Assert.All(saved, r =>
        {
            var values = r.Query.Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => (string?)Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
            var spec = ReportSpecUrl.FromQuery(values, today.AddDays(-90), today);
            Assert.Empty(ReportSpecRules.Check(spec));
        });
    }

    [Fact]
    public async Task Seeds_the_expiration_toggle_on_because_the_labels_mean_nothing_without_it()
    {
        // The sample pantry's labels are inert unless the household setting is on, and it ships off for a
        // real household. Seeding it on is what makes the expiration panel, the due-date cap, the expired
        // pin and Waste watch render at all — so it is asserted here rather than left to a page's default.
        using var db = new TestDb();
        var result = await _seeding.Seeder(db).SeedAsync();

        await using var read = db.CreateDbContext();
        var setting = Assert.Single(read.AppSettings.ToList(), s => s.Key == SettingKeys.TrackExpirationDates);
        Assert.Equal("true", setting.Value);
        // And it says so: a setting that changed itself silently is worse than a feature nobody found.
        Assert.Contains("Expiration tracking is on", result.Message);
    }

    [Fact]
    public async Task Seeds_swap_clouds_and_origins_that_cost_no_api_call()
    {
        using var db = new TestDb();
        await _seeding.Seeder(db).SeedAsync();
        await using var read = db.CreateDbContext();

        // The ⇄ swap cloud is generated once and cached on the ingredient, so an un-cached one needs an
        // AI call to open — which made the whole feature dead on a keyless demo, i.e. most of them.
        var mains = read.RecipeIngredients.Where(i => i.IsMain && i.Recipe!.ParentRecipeId == null).ToList();
        Assert.NotEmpty(mains);
        Assert.All(mains, i =>
            Assert.NotEmpty(JsonSerializer.Deserialize<List<string>>(i.AlternativesJson!)!));

        // Provenance for "remove this receipt": a product the receipt introduced goes with it (when it
        // gathered nothing else), everything else keeps its rows. Census stock is the exception that
        // states itself — no receipt ever saw it.
        var products = read.Products.Include(p => p.Purchases).ToList();
        Assert.All(products.Where(p => p.Purchases.Any(x => x.Source == PurchaseSource.Receipt)),
            p => Assert.NotNull(p.CreatedByReceiptId));
        Assert.All(products.Where(p => p.Purchases.Count == 0), p => Assert.Null(p.CreatedByReceiptId));

        // Every confirm is stamped with WHEN it ran — distinct from the date printed on the receipt, and
        // what lets removal order itself against a later human count.
        Assert.All(read.Receipts.Where(r => r.Status == ReceiptStatus.Confirmed).ToList(),
            r => Assert.NotNull(r.ConfirmedAt));

        // All three purchase sources, and two merchants — aliases are keyed per merchant, so a
        // single-store catalog can never show the same shorthand meaning different things in two shops.
        Assert.Empty(Enum.GetValues<PurchaseSource>().Except(read.PurchaseEvents.Select(p => p.Source).Distinct()));
        Assert.True(read.Receipts.Select(r => r.Merchant).Distinct().Count() > 1);
    }

    [Fact]
    public async Task Seeds_over_a_household_that_already_chose_a_setting()
    {
        // The guard is about the CATALOG, so an empty-pantry household can still hold settings rows: the
        // Settings page writes one the moment anyone touches the expiration toggle, and the sample data
        // button stays on offer the whole time. Blind-inserting the same key would collide on the
        // composite primary key (HouseholdId, Key) and take the whole seed down with it — in a first-run
        // flow, which is the only flow this button has.
        using var db = new TestDb();
        await using (var setup = db.CreateDbContext())
        {
            setup.AppSettings.Add(new AppSetting { Key = SettingKeys.TrackExpirationDates, Value = "false" });
            await setup.SaveChangesAsync();
        }

        var result = await _seeding.Seeder(db).SeedAsync();

        Assert.True(result.Seeded);
        await using var read = db.CreateDbContext();
        // The sample pantry's labels are inert without it, and the load message says it was turned on,
        // so the seed's value has to win over the pre-existing one rather than either crashing or
        // quietly leaving the feature dark while claiming otherwise.
        var setting = Assert.Single(read.AppSettings.ToList(), s => s.Key == SettingKeys.TrackExpirationDates);
        Assert.Equal("true", setting.Value);
    }

    [Fact]
    public async Task Skips_when_the_catalog_already_has_products()
    {
        using var db = new TestDb();
        var seeder = _seeding.Seeder(db);
        await seeder.SeedAsync();

        var again = await seeder.SeedAsync(); // the guard: it must never clobber existing (real) data

        Assert.False(again.Seeded);
        Assert.Equal(0, again.Products);
    }
}
