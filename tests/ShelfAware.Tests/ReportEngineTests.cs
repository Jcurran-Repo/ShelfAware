using ShelfAware.Core.Domain;
using ShelfAware.Core.Reporting;

namespace ShelfAware.Tests;

public class ReportEngineTests
{
    private static readonly DateOnly Jun1 = new(2026, 6, 1);
    private static readonly DateOnly Jul31 = new(2026, 7, 31);

    /// <summary>One fact, priced $3.50 unless said otherwise. Paid price defaults to the price;
    /// <paramref name="estimateOnly"/> models a purchase whose price is an index estimate (counts
    /// for spend, never for the UnitPrice metric).</summary>
    private static PurchaseFact Buy(
        int day, int month = 6, int productId = 1, string name = "Whole Milk",
        Category category = Category.Dairy, decimal qty = 1, decimal? price = 3.50m,
        decimal? paid = null, bool estimateOnly = false, bool dominant = true, string[]? tags = null) =>
        new(new DateOnly(2026, month, day), productId, name, category, qty, price,
            estimateOnly ? null : paid ?? price, dominant, tags ?? []);

    private static MealFact Meal(int day, int month = 6, int recipeId = 1, string name = "Tacos", int? kcal = 600) =>
        new(new DateOnly(2026, month, day), recipeId, name, kcal);

    private static ReportSpec Spec(ReportMetric metric = ReportMetric.Spend, ReportSplit split = ReportSplit.None) =>
        new() { Metric = metric, Split = split, Grain = ReportGrain.Monthly, From = Jun1, To = Jul31 };

    // ---- Bucketing -----------------------------------------------------------------------------

    [Fact]
    public void Buckets_are_continuous_calendar_periods_and_empty_ones_read_zero()
    {
        // Purchases only in June and August-adjacent July edge — July must still exist, at 0.
        var result = ReportEngine.Run(
            Spec() with { To = new DateOnly(2026, 8, 31) },
            [Buy(5), Buy(20, month: 8)], []);

        Assert.Equal(["Jun", "Jul", "Aug"], result.Buckets.Select(b => b.Label));
        var values = result.Series.Single().Values;
        Assert.Equal(3.50m, values[0]);
        Assert.Equal(0m, values[1]); // an empty month IS zero spend, not a gap
        Assert.Equal(3.50m, values[2]);
    }

    [Fact]
    public void Weekly_buckets_start_on_Monday()
    {
        // 2026-06-03 is a Wednesday; its week starts Monday 2026-06-01.
        Assert.Equal(new DateOnly(2026, 6, 1), ReportEngine.BucketStart(new DateOnly(2026, 6, 3), ReportGrain.Weekly));
        // A Monday is its own week start; Sunday belongs to the week before.
        Assert.Equal(new DateOnly(2026, 6, 1), ReportEngine.BucketStart(new DateOnly(2026, 6, 1), ReportGrain.Weekly));
        Assert.Equal(new DateOnly(2026, 6, 1), ReportEngine.BucketStart(new DateOnly(2026, 6, 7), ReportGrain.Weekly));
    }

    [Fact]
    public void Labels_carry_the_year_only_when_the_window_crosses_years()
    {
        var oneYear = ReportEngine.Run(Spec(), [], []);
        Assert.All(oneYear.Buckets, b => Assert.DoesNotContain("'", b.Label));

        var twoYears = ReportEngine.Run(
            Spec() with { From = new DateOnly(2025, 12, 1), To = new DateOnly(2026, 1, 31) }, [], []);
        Assert.Equal(["Dec '25", "Jan '26"], twoYears.Buckets.Select(b => b.Label));
    }

    // ---- Metrics -------------------------------------------------------------------------------

    [Fact]
    public void Spend_is_price_times_quantity_and_skips_unpriced_with_a_note()
    {
        var result = ReportEngine.Run(Spec(),
            [Buy(5, qty: 2, price: 4.00m), Buy(6, price: null)], []);

        Assert.Equal(8.00m, result.Total);
        Assert.Contains("1 unpriced purchase", result.Note);
    }

    [Fact]
    public void Quantity_for_one_product_sums_and_totals()
    {
        var result = ReportEngine.Run(
            Spec(ReportMetric.Quantity) with { ProductId = 1 },
            [Buy(5, qty: 2), Buy(10, qty: 1.5m)], []);

        Assert.Equal(3.5m, result.Total);
    }

    [Fact]
    public void Unit_price_averages_only_dominant_bucket_paid_prices_and_gaps_empty_months()
    {
        var result = ReportEngine.Run(
            Spec(ReportMetric.UnitPrice) with { ProductId = 1 },
            [
                Buy(5, paid: 3.00m),
                Buy(6, paid: 4.00m),
                Buy(7, paid: 99.00m, dominant: false),   // the $8 bag must not average with loose limes
                Buy(8, estimateOnly: true, price: 3.10m), // an estimate is not a paid price
            ], []);

        var values = result.Series.Single().Values;
        Assert.Equal(3.50m, values[0]); // (3+4)/2 — dominant paid prices only
        Assert.Null(values[1]);         // no July purchases: a GAP, not "it was free"
        Assert.Null(result.Total);      // averaging's sum means nothing
        Assert.False(result.Additive);
    }

    [Fact]
    public void Meals_count_and_calories_sum_with_unknowns_disclosed()
    {
        var meals = new[] { Meal(5), Meal(12), Meal(20, kcal: null) };

        var count = ReportEngine.Run(Spec(ReportMetric.MealsCooked), [], meals);
        Assert.Equal(3m, count.Total);
        Assert.Null(count.Note); // every meal counts for the count

        var kcal = ReportEngine.Run(Spec(ReportMetric.Calories), [], meals);
        Assert.Equal(1200m, kcal.Total);
        Assert.Contains("no calorie estimate", kcal.Note);
    }

    // ---- Filters -------------------------------------------------------------------------------

    [Fact]
    public void Subject_filters_narrow_by_category_product_and_tag()
    {
        var facts = new[]
        {
            Buy(5, productId: 1, name: "Whole Milk", category: Category.Dairy, tags: ["staple"]),
            Buy(6, productId: 2, name: "Goldfish", category: Category.Pantry, tags: ["snack", "kids"]),
            Buy(7, productId: 3, name: "Apples", category: Category.Produce),
        };

        Assert.Equal(3.50m, ReportEngine.Run(Spec() with { Category = Category.Pantry }, facts, []).Total);
        Assert.Equal(3.50m, ReportEngine.Run(Spec() with { ProductId = 3 }, facts, []).Total);
        Assert.Equal(3.50m, ReportEngine.Run(Spec() with { Tag = "kids" }, facts, []).Total);
        // Tag matching is case-insensitive — tags are human-typed.
        Assert.Equal(3.50m, ReportEngine.Run(Spec() with { Tag = "KIDS" }, facts, []).Total);
    }

    // ---- Splits --------------------------------------------------------------------------------

    [Fact]
    public void Category_split_partitions_stacks_and_totals()
    {
        var result = ReportEngine.Run(Spec(split: ReportSplit.ByCategory),
            [Buy(5, category: Category.Dairy), Buy(6, productId: 2, name: "Apples", category: Category.Produce)], []);

        Assert.Equal(2, result.Series.Count);
        Assert.True(result.Stackable);
        Assert.Equal(7.00m, result.Total);
    }

    [Fact]
    public void Category_split_beyond_top_N_pools_so_the_stack_and_total_stay_complete()
    {
        // Caught live: small categories were DROPPED from the stacked chart, silently understating
        // the stack against the spend tiles beside it. A partitioning split must pool, never drop.
        var facts = Enum.GetValues<Category>()
            .Select((c, i) => Buy(5 + i, productId: 100 + i, name: $"Item {i}", category: c, price: 10m + i))
            .ToArray();
        var result = ReportEngine.Run(
            Spec(split: ReportSplit.ByCategory) with { TopN = 4 }, facts, []);

        Assert.Equal(5, result.Series.Count);
        Assert.Equal("Everything else", result.Series[^1].Label);
        Assert.Equal(facts.Sum(f => f.Price!.Value), result.Total); // nothing vanished
        Assert.Null(result.Note); // pooled, so there's no dropped-series disclosure to make
    }

    [Fact]
    public void Product_split_keeps_top_N_by_spend_and_pools_the_rest()
    {
        var facts = new[]
        {
            Buy(5, productId: 1, name: "Steak", price: 20m),
            Buy(6, productId: 2, name: "Milk", price: 4m),
            Buy(7, productId: 3, name: "Gum", price: 1m),
            Buy(8, productId: 4, name: "Salt", price: 0.50m),
        };
        var result = ReportEngine.Run(
            Spec(split: ReportSplit.ByProduct) with { TopN = 2 }, facts, []);

        Assert.Equal(["Steak", "Milk", "Everything else"], result.Series.Select(s => s.Label));
        Assert.Equal(1.50m, result.Series[2].Total); // the pool is the real remainder, not a dropped one
        Assert.Equal(25.50m, result.Total);          // pooling keeps the window total complete
    }

    [Fact]
    public void Tag_series_overlap_never_stack_and_never_total()
    {
        var facts = new[]
        {
            Buy(5, productId: 2, name: "Goldfish", price: 3m, tags: ["snack", "kids"]),
            Buy(6, productId: 5, name: "Chips", price: 4m, tags: ["snack"]),
            Buy(7, productId: 6, name: "Batteries", price: 9m), // untagged
        };
        var result = ReportEngine.Run(Spec(split: ReportSplit.ByTag), facts, []);

        // Goldfish counts in BOTH tag series — the overlap is the point of comparing tags…
        Assert.Equal(7m, result.Series.Single(s => s.Label == "snack").Total);
        Assert.Equal(3m, result.Series.Single(s => s.Label == "kids").Total);
        Assert.Equal(9m, result.Series.Single(s => s.Label == "(untagged)").Total);
        // …and exactly why no stacked chart and no grand total are offered.
        Assert.False(result.Stackable);
        Assert.Null(result.Total);
    }

    [Fact]
    public void Quantity_split_by_product_neither_pools_nor_totals_but_discloses()
    {
        var facts = new[]
        {
            Buy(5, productId: 1, name: "Beef", qty: 3),
            Buy(6, productId: 2, name: "Limes", qty: 2),
            Buy(7, productId: 3, name: "Milk", qty: 1),
        };
        var result = ReportEngine.Run(
            Spec(ReportMetric.Quantity, ReportSplit.ByProduct) with { TopN = 2 }, facts, []);

        Assert.Equal(2, result.Series.Count);            // no "Everything else" — 3 lb + 2 limes isn't a number
        Assert.Null(result.Total);
        Assert.False(result.Stackable);
        Assert.Contains("1 more product", result.Note);  // but the cut is disclosed
    }

    [Fact]
    public void Recipe_split_pools_the_remainder()
    {
        var meals = new[]
        {
            Meal(5, recipeId: 1, name: "Tacos"), Meal(12, recipeId: 1, name: "Tacos"),
            Meal(6, recipeId: 2, name: "Chicken & Rice"),
            Meal(7, recipeId: 3, name: "Marinara"),
        };
        var result = ReportEngine.Run(
            Spec(ReportMetric.MealsCooked, ReportSplit.ByRecipe) with { TopN = 2 }, [], meals);

        Assert.Equal(["Tacos", "Chicken & Rice", "Everything else"], result.Series.Select(s => s.Label));
        Assert.Equal(4m, result.Total);
    }

    // ---- Compare-previous ----------------------------------------------------------------------

    [Fact]
    public void Compare_previous_totals_the_equal_length_window_before_From()
    {
        var result = ReportEngine.Run(
            Spec() with { From = new DateOnly(2026, 7, 1), To = Jul31, ComparePrevious = true },
            [
                Buy(10, month: 7, price: 12m),     // in the window
                Buy(15, month: 6, price: 5m),      // in the 31 days before it
                Buy(20, month: 4, price: 100m),    // long before either
            ], []);

        Assert.Equal(12m, result.Total);
        Assert.Equal(5m, result.PreviousTotal);
    }

    // ---- Rules ---------------------------------------------------------------------------------

    [Fact]
    public void Unsound_specs_are_refused_not_charted()
    {
        // The rules list explains; Run enforces. Both must agree — a UI bug can't produce a lying chart.
        var crossProductQuantity = Spec(ReportMetric.Quantity);
        Assert.NotEmpty(ReportSpecRules.Check(crossProductQuantity));
        Assert.Throws<ArgumentException>(() => ReportEngine.Run(crossProductQuantity, [], []));

        Assert.NotEmpty(ReportSpecRules.Check(Spec(ReportMetric.UnitPrice)));                       // no product
        Assert.NotEmpty(ReportSpecRules.Check(Spec(split: ReportSplit.ByTag) with { Chart = ReportChart.StackedBars }));
        Assert.NotEmpty(ReportSpecRules.Check(Spec(ReportMetric.MealsCooked, ReportSplit.ByProduct)));
        Assert.NotEmpty(ReportSpecRules.Check(Spec() with { To = Jun1.AddDays(-1) }));
        Assert.NotEmpty(ReportSpecRules.Check(Spec(ReportMetric.Spend) with { RecipeId = 3 }));

        // TopN's upper bound is about chart color slots, so it binds charts and spares tables —
        // regression pin: the first version of this rule 500'd the report card's top-10 table.
        Assert.NotEmpty(ReportSpecRules.Check(Spec(split: ReportSplit.ByProduct) with { TopN = 9 }));
        Assert.Empty(ReportSpecRules.Check(Spec(split: ReportSplit.ByProduct) with { TopN = ReportSpecRules.MaxTopN }));
        Assert.Empty(ReportSpecRules.Check(Spec(split: ReportSplit.ByProduct) with { TopN = 10, Chart = ReportChart.Table }));

        Assert.Empty(ReportSpecRules.Check(Spec()));
        Assert.Empty(ReportSpecRules.Check(Spec(ReportMetric.Quantity, ReportSplit.ByProduct)));
        Assert.Empty(ReportSpecRules.Check(Spec(split: ReportSplit.ByCategory) with { Chart = ReportChart.StackedBars }));
    }
}
