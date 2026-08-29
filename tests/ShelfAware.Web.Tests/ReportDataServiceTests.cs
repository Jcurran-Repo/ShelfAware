using ShelfAware.Core.Domain;
using ShelfAware.Core.Reporting;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The preset loads that used to live in Reports.razor, where nothing could reach them. That gap was not
/// theoretical: the backlog rows shipped re-deriving a due date from the rebuy median instead of asking
/// the engine, which called an item overdue while its own product page called it Stocked — past a fully
/// green suite. These pin the joins against real EF on real SQLite.
/// </summary>
public class ReportDataServiceTests
{
    private static readonly DateOnly Today = new(2026, 7, 28);

    private static DateOnly DaysAgo(int days) => Today.AddDays(-days);

    /// <summary>Seeds a product with its buying history. <paramref name="unitPrice"/> also writes a
    /// confirmed receipt line per buy, which is what gives the report facts a price to work from.</summary>
    private static async Task<Product> SeedProductAsync(
        TestDb db,
        string name,
        (int DaysAgo, decimal Qty)[] buys,
        int[]? outageDaysAgo = null,
        bool tracked = true,
        decimal? unitPrice = null,
        Category category = Category.Pantry)
    {
        await using var ctx = db.CreateDbContext();
        var product = new Product { Name = name, Category = category, IsTracked = tracked };
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        foreach (var (daysAgo, qty) in buys)
        {
            var date = DaysAgo(daysAgo);
            int? receiptId = null;
            if (unitPrice is { } price)
            {
                var receipt = new Receipt
                {
                    ImagePath = $"{name}-{daysAgo}",
                    PurchasedAt = date,
                    Status = ReceiptStatus.Confirmed,
                };
                ctx.Receipts.Add(receipt);
                await ctx.SaveChangesAsync();
                ctx.ReceiptLines.Add(new ReceiptLine
                {
                    ReceiptId = receipt.Id,
                    RawText = name,
                    NormalizedName = name,
                    Quantity = qty,
                    UnitPrice = price,
                    ProductId = product.Id,
                    Category = category,
                });
                receiptId = receipt.Id;
            }

            ctx.PurchaseEvents.Add(new PurchaseEvent
            {
                ProductId = product.Id,
                PurchasedAt = date,
                Quantity = qty,
                ReceiptId = receiptId,
                Source = PurchaseSource.Receipt,
            });
        }

        foreach (var daysAgo in outageDaysAgo ?? [])
        {
            ctx.InventorySignals.Add(new InventorySignal
            {
                ProductId = product.Id,
                Kind = SignalKind.OutNow,
                SignaledAt = new DateTimeOffset(DaysAgo(daysAgo).ToDateTime(TimeOnly.MinValue)),
            });
        }

        await ctx.SaveChangesAsync();
        return product;
    }

    /// <summary>Bought one at a time on a ~14-day rhythm, then N at once <paramref name="lastBuyDaysAgo"/>
    /// days ago. The stock-up is what makes the engine's due date differ from the plain median.</summary>
    private static (int DaysAgo, decimal Qty)[] StockUpHistory(int lastBuyDaysAgo, decimal bulkQty) =>
        [(lastBuyDaysAgo, bulkQty),
         (lastBuyDaysAgo + 14, 1), (lastBuyDaysAgo + 28, 1),
         (lastBuyDaysAgo + 42, 1), (lastBuyDaysAgo + 56, 1)];

    // ---- The backlog check ----------------------------------------------------------------------

    [Fact]
    public async Task A_stock_up_that_pushed_the_due_date_out_is_not_called_overdue()
    {
        // THE REGRESSION, at the layer it actually shipped in. Last bought 20 days ago on a 14-day
        // rebuy rhythm, so "days since last buy > rebuy median" says overdue — but the buy was 6× the
        // usual, so StockUpFactor stretched the projection to ~84 days and the engine says it isn't due
        // for another two months. Asking the engine is the only thing that gets this right.
        using var db = new TestDb();
        await SeedProductAsync(db, "Beef Chuck Roast", StockUpHistory(lastBuyDaysAgo: 20, bulkQty: 6));

        var service = new ReportDataService(db);
        var report = await service.LoadBacklogAsync(
            await service.LoadAsync(), await service.LoadRecipeMainsAsync(), Today, honorExpirations: false, 60);

        Assert.Equal(1, report.Considered); // judged, and deliberately rejected
        Assert.DoesNotContain(report.Findings, f => f.ProductName == "Beef Chuck Roast");
    }

    [Fact]
    public async Task A_real_hoard_is_found_once_even_the_stretched_due_date_has_passed()
    {
        // Same shape, months older: the ~84-day stretch has run out, so the grocery list is asking for a
        // roast the freezer may still be full of. That's the finding.
        using var db = new TestDb();
        await SeedProductAsync(db, "Beef Chuck Roast", StockUpHistory(lastBuyDaysAgo: 130, bulkQty: 6));

        var service = new ReportDataService(db);
        var report = await service.LoadBacklogAsync(
            await service.LoadAsync(), await service.LoadRecipeMainsAsync(), Today, honorExpirations: false, 60);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("Beef Chuck Roast", finding.ProductName);
        Assert.Equal(5, finding.Trips);
        Assert.True(finding.OverdueDays > 30, $"expected long overdue, got {finding.OverdueDays}");
        Assert.Equal(0, report.EverRanOut);
    }

    [Fact]
    public async Task The_backlog_check_answers_the_same_due_question_the_dashboard_asks()
    {
        // The page tells the reader "Overdue by is the same number the dashboard and the item's own page
        // show". That's only true if this asks the question the same way — so it threads the household's
        // expiration setting rather than running blind. Here the rhythm says two more months, but the
        // label passed ten days ago: an opted-in household sees the item as due everywhere, and this
        // report must agree with the rest of the app rather than quietly disagreeing.
        using var db = new TestDb();
        var roast = await SeedProductAsync(db, "Beef Chuck Roast", StockUpHistory(lastBuyDaysAgo: 20, bulkQty: 6));
        await using (var ctx = db.CreateDbContext())
        {
            var latest = ctx.PurchaseEvents
                .Where(p => p.ProductId == roast.Id)
                .OrderByDescending(p => p.PurchasedAt)
                .First();
            latest.ExpirationDate = DaysAgo(10);
            await ctx.SaveChangesAsync();
        }

        var service = new ReportDataService(db);
        var source = await service.LoadAsync();
        var mains = await service.LoadRecipeMainsAsync();

        var optedOut = await service.LoadBacklogAsync(source, mains, Today, honorExpirations: false, 60);
        var optedIn = await service.LoadBacklogAsync(source, mains, Today, honorExpirations: true, 60);

        Assert.Empty(optedOut.Findings); // the rhythm alone says it isn't due for two more months
        Assert.Equal(10, Assert.Single(optedIn.Findings).OverdueDays); // the label the rest of the app honours
    }

    [Fact]
    public async Task An_untracked_product_is_never_a_finding()
    {
        // Untracking means "don't want it for a while", so it's quiet past its rhythm by construction —
        // listing it would re-nag about exactly what the household asked to stop hearing about.
        using var db = new TestDb();
        await SeedProductAsync(db, "Beef Chuck Roast", StockUpHistory(130, 6), tracked: false);

        var service = new ReportDataService(db);
        var report = await service.LoadBacklogAsync(
            await service.LoadAsync(), await service.LoadRecipeMainsAsync(), Today, honorExpirations: false, 60);

        Assert.Equal(0, report.Considered);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task An_item_that_ran_out_once_is_judged_and_rejected()
    {
        using var db = new TestDb();
        // 137 days ago sits BETWEEN the buys at 144 and 130, so it closes a cycle. (On the same day as a
        // buy it wouldn't: same-day ties go to the purchase, which is the engine's documented rule.)
        await SeedProductAsync(db, "Ground Coffee", StockUpHistory(130, 6), outageDaysAgo: [137]);

        var service = new ReportDataService(db);
        var report = await service.LoadBacklogAsync(
            await service.LoadAsync(), await service.LoadRecipeMainsAsync(), Today, honorExpirations: false, 60);

        Assert.Equal(1, report.Considered);
        Assert.Equal(1, report.EverRanOut);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task Spend_is_unit_price_times_quantity_not_the_unit_price()
    {
        // The Spend metric's rule (ReportEngine.PurchaseValue). Ten packages at $10 is $100, and a
        // report that quoted $10 would understate the money sitting in the freezer tenfold.
        using var db = new TestDb();
        await SeedProductAsync(db, "Beef Chuck Roast", StockUpHistory(130, 6), unitPrice: 10m);

        var service = new ReportDataService(db);
        var report = await service.LoadBacklogAsync(
            await service.LoadAsync(), await service.LoadRecipeMainsAsync(), Today, honorExpirations: false, 60);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(10m, finding.TotalQuantity); // 6 + 1 + 1 + 1 + 1
        Assert.Equal(100m, finding.Spend);
        Assert.False(finding.SpendIncomplete);
    }

    [Fact]
    public async Task An_unpriced_purchase_marks_the_spend_as_a_floor()
    {
        using var db = new TestDb();
        await SeedProductAsync(db, "Beef Chuck Roast", StockUpHistory(130, 6)); // no receipt lines at all

        var service = new ReportDataService(db);
        var report = await service.LoadBacklogAsync(
            await service.LoadAsync(), await service.LoadRecipeMainsAsync(), Today, honorExpirations: false, 60);

        Assert.True(Assert.Single(report.Findings).SpendIncomplete);
    }

    [Fact]
    public async Task Cooked_with_counts_recent_meals_by_matched_product_name()
    {
        // The join that isn't obvious: recipes point at products by NAME (RecipeIngredient.MatchedProduct),
        // not by id, and each meal counts once per main ingredient. The window is the caller's.
        using var db = new TestDb();
        var roast = await SeedProductAsync(db, "Beef Chuck Roast", StockUpHistory(130, 6));

        await using (var ctx = db.CreateDbContext())
        {
            var recipe = new Recipe { Name = "Pot Roast" };
            recipe.Ingredients.Add(new RecipeIngredient
            {
                Name = "beef chuck roast",
                IsMain = true,
                MatchedProduct = roast.Name,
            });
            ctx.Recipes.Add(recipe);
            await ctx.SaveChangesAsync();

            ctx.MealEvents.Add(new MealEvent { RecipeId = recipe.Id, AteAt = DaysAgo(10) });
            ctx.MealEvents.Add(new MealEvent { RecipeId = recipe.Id, AteAt = DaysAgo(30) });
            ctx.MealEvents.Add(new MealEvent { RecipeId = recipe.Id, AteAt = DaysAgo(200) }); // outside the window
            await ctx.SaveChangesAsync();
        }

        var service = new ReportDataService(db);
        var report = await service.LoadBacklogAsync(
            await service.LoadAsync(), await service.LoadRecipeMainsAsync(), Today, honorExpirations: false, 60);

        Assert.Equal(2, Assert.Single(report.Findings).RecentMealUses);
    }

    [Fact]
    public async Task Cooked_with_counts_a_meal_grounded_by_a_PUNCTUATION_variant()
    {
        // The join keys on product IDENTITY, not the raw name. MatchedProduct is captured at save time,
        // so a punctuation variant ("Home Canned Sauce" for "Home-Canned Sauce") is the same product to
        // every other surface — "recipes that use this" lists it, so the "cooked with" count must too, or
        // two surfaces of one app disagree. A raw OrdinalIgnoreCase compare read 0 here.
        using var db = new TestDb();
        await SeedProductAsync(db, "Home-Canned Sauce", StockUpHistory(130, 6));

        await using (var ctx = db.CreateDbContext())
        {
            var recipe = new Recipe { Name = "Pasta Night" };
            recipe.Ingredients.Add(new RecipeIngredient
            {
                Name = "sauce",
                IsMain = true,
                MatchedProduct = "Home Canned Sauce", // grounded to the variant spelling, not the product name
            });
            ctx.Recipes.Add(recipe);
            await ctx.SaveChangesAsync();

            ctx.MealEvents.Add(new MealEvent { RecipeId = recipe.Id, AteAt = DaysAgo(10) });
            ctx.MealEvents.Add(new MealEvent { RecipeId = recipe.Id, AteAt = DaysAgo(30) });
            await ctx.SaveChangesAsync();
        }

        var service = new ReportDataService(db);
        var report = await service.LoadBacklogAsync(
            await service.LoadAsync(), await service.LoadRecipeMainsAsync(), Today, honorExpirations: false, 60);

        Assert.Equal(2, Assert.Single(report.Findings).RecentMealUses);
    }

    // ---- The gap report -------------------------------------------------------------------------

    [Fact]
    public async Task Gap_rows_need_both_rhythms_and_lead_with_the_widest_gap()
    {
        using var db = new TestDb();
        // Two closed burn cycles (bought day 60 → out day 50, bought day 40 → out day 30) give a burn
        // rate of 10 against a 20-day rebuy rhythm: out for ~10 days every cycle.
        await SeedProductAsync(db, "Dish Soap", [(60, 1), (40, 1), (20, 1)], outageDaysAgo: [50, 30]);
        // Tighter gap: out only ~4 days before the rebuy.
        await SeedProductAsync(db, "Paper Towels", [(60, 1), (40, 1), (20, 1)], outageDaysAgo: [44, 24]);
        // No outages at all → no burn rate → no gap to state.
        await SeedProductAsync(db, "Ground Coffee", [(60, 1), (40, 1), (20, 1)]);

        var rows = await new ReportDataService(db).LoadGapRowsAsync(Today, honorExpirations: false);

        Assert.Equal(new[] { "Dish Soap", "Paper Towels" }, rows.Select(r => r.Name).ToArray());
        Assert.Equal(10, rows[0].Burn);
        Assert.Equal(20, rows[0].Rebuy);
        Assert.Equal(10, rows[0].Gap);
    }

    // ---- Waste watch ----------------------------------------------------------------------------

    [Fact]
    public async Task A_label_that_passed_with_no_evidence_is_worth_checking_and_carries_its_price()
    {
        using var db = new TestDb();
        var milk = await SeedProductAsync(db, "Whole Milk", [(30, 2)], unitPrice: 4m, category: Category.Dairy);
        await using (var ctx = db.CreateDbContext())
        {
            var purchase = ctx.PurchaseEvents.Single(p => p.ProductId == milk.Id);
            purchase.ExpirationDate = DaysAgo(20); // label passed, and nothing says it was finished
            await ctx.SaveChangesAsync();
        }

        var service = new ReportDataService(db);
        var outcomes = await service.LoadLabelOutcomesAsync(await service.LoadAsync(), Today);

        var judged = Assert.Single(outcomes);
        Assert.Equal(LabelOutcome.PassedQuietly, judged.Outcome);
        Assert.Equal(4m, judged.Purchase.Price); // priced from the facts, not re-queried
    }

    [Fact]
    public async Task Two_dated_buys_on_one_day_keep_their_own_prices()
    {
        // Two trips in a day is a real shape, and pricing the facts by (product, date) collapsed them:
        // both rows took whichever came first. The join keys on the PURCHASE now.
        using var db = new TestDb();
        var milk = await SeedProductAsync(db, "Whole Milk", [(30, 1)], unitPrice: 3.49m, category: Category.Dairy);
        await SeedSecondSameDayBuyAsync(db, milk, DaysAgo(30), unitPrice: 1.99m);
        await using (var ctx = db.CreateDbContext())
        {
            foreach (var p in ctx.PurchaseEvents.Where(p => p.ProductId == milk.Id).ToList())
                p.ExpirationDate = DaysAgo(20);
            await ctx.SaveChangesAsync();
        }

        var service = new ReportDataService(db);
        var outcomes = await service.LoadLabelOutcomesAsync(await service.LoadAsync(), Today);

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(
            new[] { 1.99m, 3.49m },
            outcomes.Select(o => o.Purchase.Price!.Value).OrderBy(p => p).ToArray());
    }

    /// <summary>A second trip on the same day at a different price — its own receipt, because a
    /// PurchaseEvent points at a receipt rather than a line, so two lines on ONE receipt genuinely
    /// share an averaged price and no join can separate them.</summary>
    private static async Task SeedSecondSameDayBuyAsync(TestDb db, Product product, DateOnly date, decimal unitPrice)
    {
        await using var ctx = db.CreateDbContext();
        var receipt = new Receipt { ImagePath = $"{product.Name}-second", PurchasedAt = date, Status = ReceiptStatus.Confirmed };
        ctx.Receipts.Add(receipt);
        await ctx.SaveChangesAsync();
        ctx.ReceiptLines.Add(new ReceiptLine
        {
            ReceiptId = receipt.Id,
            RawText = product.Name,
            NormalizedName = product.Name,
            Quantity = 1,
            UnitPrice = unitPrice,
            ProductId = product.Id,
            Category = product.Category,
        });
        ctx.PurchaseEvents.Add(new PurchaseEvent
        {
            ProductId = product.Id,
            PurchasedAt = date,
            Quantity = 1,
            ReceiptId = receipt.Id,
            Source = PurchaseSource.Receipt,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task A_purchase_marked_out_before_its_label_was_finished_not_lost()
    {
        using var db = new TestDb();
        var milk = await SeedProductAsync(db, "Whole Milk", [(30, 1)], outageDaysAgo: [25], category: Category.Dairy);
        await using (var ctx = db.CreateDbContext())
        {
            var purchase = ctx.PurchaseEvents.Single(p => p.ProductId == milk.Id);
            purchase.ExpirationDate = DaysAgo(20);
            await ctx.SaveChangesAsync();
        }

        var service = new ReportDataService(db);
        var outcomes = await service.LoadLabelOutcomesAsync(await service.LoadAsync(), Today);

        Assert.Equal(LabelOutcome.MarkedOut, Assert.Single(outcomes).Outcome);
    }

    [Fact]
    public async Task A_product_priced_only_at_zero_loads_without_crashing_and_has_no_current_price()
    {
        // A $0.00 line is a coupon/void/misread, not a price — PriceSeries.Dominant returns null for a
        // product with no positive line. LoadAsync must skip it, not leave a null in the price map that
        // the current-price join and the inDominant check then dereference (both threw on a null series).
        using var db = new TestDb();
        await SeedProductAsync(db, "Free Sample", [(10, 1)], unitPrice: 0m);
        await SeedProductAsync(db, "Real Yogurt", [(20, 1), (5, 1)], unitPrice: 4m);

        var source = await new ReportDataService(db).LoadAsync(); // a null series threw building this

        Assert.Contains("Real Yogurt", source.CurrentPriceByProductName.Keys);      // the priced product is priced
        Assert.DoesNotContain("Free Sample", source.CurrentPriceByProductName.Keys); // the $0-only product is skipped
    }

    [Fact]
    public async Task No_dated_purchases_is_an_empty_list_not_a_query_over_everything()
    {
        using var db = new TestDb();
        await SeedProductAsync(db, "Whole Milk", [(30, 1), (20, 1)]);

        var service = new ReportDataService(db);

        Assert.Empty(await service.LoadLabelOutcomesAsync(await service.LoadAsync(), Today));
    }
}
