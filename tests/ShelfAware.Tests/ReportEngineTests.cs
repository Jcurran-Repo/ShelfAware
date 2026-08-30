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
    private static int nextPurchaseId;

    private static PurchaseFact Buy(
        int day, int month = 6, int productId = 1, string name = "Whole Milk",
        Category category = Category.Dairy, decimal qty = 1, decimal? price = 3.50m,
        decimal? paid = null, bool estimateOnly = false, bool dominant = true, string[]? tags = null) =>
        new(++nextPurchaseId, new DateOnly(2026, month, day), productId, name, category, qty, price,
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

    [Fact]
    public void Purchase_count_counts_trips_to_the_till_and_totals()
    {
        // The one purchase metric with no test until the 7/30 audit: it counts FACTS (line items),
        // ignores prices entirely, and — unlike quantity — is honestly additive across products.
        var result = ReportEngine.Run(Spec(ReportMetric.PurchaseCount),
            [Buy(5), Buy(6, productId: 2, name: "Apples", price: null), Buy(10, month: 7)], []);

        Assert.Equal(3m, result.Total);
        Assert.Equal([2m, 1m], result.Series.Single().Values);
        Assert.True(result.Additive);
        Assert.Null(result.Note); // an unpriced purchase still counts — no disclosure needed
    }

    [Fact]
    public void Quarterly_buckets_start_on_quarter_boundaries_and_label_as_Q()
    {
        Assert.Equal(new DateOnly(2026, 4, 1), ReportEngine.BucketStart(new DateOnly(2026, 6, 30), ReportGrain.Quarterly));
        Assert.Equal(new DateOnly(2026, 7, 1), ReportEngine.BucketStart(new DateOnly(2026, 7, 1), ReportGrain.Quarterly));

        var result = ReportEngine.Run(
            Spec() with { Grain = ReportGrain.Quarterly, From = new DateOnly(2026, 4, 1), To = new DateOnly(2026, 9, 30) },
            [Buy(5), Buy(10, month: 8)], []);

        Assert.Equal(["Q2", "Q3"], result.Buckets.Select(b => b.Label));
        Assert.Equal([3.50m, 3.50m], result.Series.Single().Values);
    }

    [Fact]
    public void Weekly_buckets_label_with_the_week_start_date()
    {
        // 2026-06-01 is a Monday, so a two-week window reads as its two Mondays.
        var result = ReportEngine.Run(
            Spec() with { Grain = ReportGrain.Weekly, From = new DateOnly(2026, 6, 1), To = new DateOnly(2026, 6, 14) },
            [Buy(3)], []);

        Assert.Equal(["Jun 1", "Jun 8"], result.Buckets.Select(b => b.Label));
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
        Assert.NotEmpty(ReportSpecRules.Check(Spec(ReportMetric.UnitPrice, ReportSplit.ByCategory) with { ProductId = 1 })); // unit price never splits
        Assert.NotEmpty(ReportSpecRules.Check(Spec(split: ReportSplit.ByTag) with { Chart = ReportChart.StackedBars }));
        Assert.NotEmpty(ReportSpecRules.Check(Spec(ReportMetric.MealsCooked, ReportSplit.ByProduct)));
        Assert.NotEmpty(ReportSpecRules.Check(Spec(split: ReportSplit.ByRecipe)));                  // recipes are a meal-metric split
        Assert.NotEmpty(ReportSpecRules.Check(Spec(ReportMetric.MealsCooked) with { Tag = "kids" })); // pantry filters don't reach the meal log
        Assert.NotEmpty(ReportSpecRules.Check(Spec() with { To = Jun1.AddDays(-1) }));
        Assert.NotEmpty(ReportSpecRules.Check(Spec(ReportMetric.Spend) with { RecipeId = 3 }));
        Assert.NotEmpty(ReportSpecRules.Check(Spec(split: ReportSplit.ByProduct) with { TopN = 0 })); // at least one series

        // TopN's upper bound is about chart color slots, so it binds charts and spares tables —
        // regression pin: the first version of this rule 500'd the report card's top-10 table.
        Assert.NotEmpty(ReportSpecRules.Check(Spec(split: ReportSplit.ByProduct) with { TopN = 9 }));
        Assert.Empty(ReportSpecRules.Check(Spec(split: ReportSplit.ByProduct) with { TopN = ReportSpecRules.MaxTopN }));
        Assert.Empty(ReportSpecRules.Check(Spec(split: ReportSplit.ByProduct) with { TopN = 10, Chart = ReportChart.Table }));

        Assert.Empty(ReportSpecRules.Check(Spec()));
        Assert.Empty(ReportSpecRules.Check(Spec(ReportMetric.Quantity, ReportSplit.ByProduct)));
        Assert.Empty(ReportSpecRules.Check(Spec(split: ReportSplit.ByCategory) with { Chart = ReportChart.StackedBars }));
    }

    [Fact]
    public void The_refusal_names_itself_and_carries_the_problems()
    {
        // The exception is operator-facing (a saved spec the engine refuses greets a visitor with an
        // error page whose log line is this message) — it must say WHAT failed, not throw bare.
        var ex = Assert.Throws<ArgumentException>(() => ReportEngine.Run(Spec(ReportMetric.Quantity), [], []));

        Assert.Contains("The report spec is not sound:", ex.Message);
        Assert.Contains("quantity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Several_problems_join_into_one_space_separated_message()
    {
        // Two violations at once — the range runs backwards AND quantity has no product. The message
        // joins the problems with a space so the operator reads a sentence stream; a "" separator would
        // jam them into "...starts.Quantity...".
        var spec = Spec(ReportMetric.Quantity) with { From = Jul31, To = Jun1 };
        var ex = Assert.Throws<ArgumentException>(() => ReportEngine.Run(spec, [], []));

        Assert.Contains("starts. Quantity only means", ex.Message);
    }

    // Each rule states WHY, and the builder UI shows exactly that reason — so pin the distinctive phrase
    // of each refusal (the "whether it fires" is already covered by Unsound_specs_are_refused above).
    [Fact]
    public void Each_rule_explains_itself()
    {
        void RuleSays(ReportSpec spec, string phrase) =>
            Assert.Contains(ReportSpecRules.Check(spec), m => m.Contains(phrase, StringComparison.OrdinalIgnoreCase));

        RuleSays(Spec() with { To = Jun1.AddDays(-1) }, "ends before it starts");
        RuleSays(Spec(ReportMetric.Quantity), "Quantity only means");
        RuleSays(Spec(ReportMetric.UnitPrice), "per item");
        RuleSays(Spec(ReportMetric.UnitPrice, ReportSplit.ByCategory) with { ProductId = 1 }, "doesn't split");
        RuleSays(Spec(ReportMetric.MealsCooked, ReportSplit.ByProduct), "split by recipe, or not at all");
        RuleSays(Spec(split: ReportSplit.ByRecipe), "Splitting by recipe only applies");
        RuleSays(Spec(ReportMetric.MealsCooked) with { Category = Category.Dairy }, "filters don't apply");
        RuleSays(Spec(ReportMetric.Spend) with { RecipeId = 3 }, "recipe filter only applies");
        RuleSays(Spec(split: ReportSplit.ByProduct) with { TopN = 0 }, "at least one series");
        RuleSays(Spec(split: ReportSplit.ByProduct) with { TopN = 9 }, "at most");
    }

    [Fact]
    public void A_single_day_window_is_allowed()
    {
        // The range check is To < From, exclusive: a report over one day (To == From) is fine.
        Assert.Empty(ReportSpecRules.Check(Spec() with { To = Jun1 }));
    }

    [Fact]
    public void A_meal_metric_refuses_a_lone_pantry_filter()
    {
        // The filter guard is an OR across category/product/tag — a category filter alone (no product,
        // no tag) is still refused. (An AND would let a lone category slip through.)
        Assert.NotEmpty(ReportSpecRules.Check(Spec(ReportMetric.MealsCooked) with { Category = Category.Dairy }));
        Assert.NotEmpty(ReportSpecRules.Check(Spec(ReportMetric.MealsCooked) with { ProductId = 5 }));
    }

    [Fact]
    public void Stacking_a_non_partitioning_split_names_the_right_reason()
    {
        // A tag stack double-counts; any other non-partitioning split (here None) just doesn't partition —
        // two different refusals, and the message must match the split.
        var tag = ReportSpecRules.Check(Spec(split: ReportSplit.ByTag) with { Chart = ReportChart.StackedBars });
        Assert.Contains(tag, m => m.Contains("double-count"));

        var none = ReportSpecRules.Check(Spec() with { Chart = ReportChart.StackedBars });
        Assert.Contains(none, m => m.Contains("partitions the data"));
    }

    // ---- The window's exact edges ---------------------------------------------------------------

    [Fact]
    public void The_purchase_window_includes_both_edge_days_and_nothing_outside()
    {
        // From and To are both INCLUSIVE — a purchase on either edge day counts; one a day outside
        // either edge never does. The window is the report's most basic honesty claim.
        var result = ReportEngine.Run(Spec(),
            [Buy(31, month: 5), Buy(1), Buy(31, month: 7), Buy(1, month: 8)], []);

        Assert.Equal(7.00m, result.Total); // exactly the Jun 1 + Jul 31 facts
    }

    [Fact]
    public void The_meal_window_includes_both_edge_days_and_filters_by_recipe()
    {
        var meals = new[] { Meal(31, month: 5), Meal(1), Meal(31, month: 7), Meal(1, month: 8), Meal(15, recipeId: 2, name: "Stew") };

        var all = ReportEngine.Run(Spec(ReportMetric.MealsCooked), [], meals);
        Assert.Equal(3m, all.Total); // Jun 1 + Jul 31 + the stew; edges in, outside out

        var one = ReportEngine.Run(Spec(ReportMetric.MealsCooked) with { RecipeId = 2 }, [], meals);
        Assert.Equal(1m, one.Total); // the recipe filter is equality, not exclusion
    }

    [Fact]
    public void Purchases_outside_the_window_form_no_series()
    {
        // The date filter is an AND — in range on BOTH edges. Splitting an in-window product against two
        // out-of-window ones yields ONE series; mutating the && to || would pull the outsiders in as ghost
        // series (all-zero across the buckets, but present). The bucketed Total can't catch this — an
        // out-of-window fact maps to no bucket and adds nothing to it — so the series list is the observable.
        var result = ReportEngine.Run(Spec(ReportMetric.PurchaseCount, ReportSplit.ByProduct),
            [Buy(15), Buy(15, month: 5, productId: 2, name: "Before"), Buy(15, month: 8, productId: 3, name: "After")], []);

        Assert.Equal(["Whole Milk"], result.Series.Select(s => s.Label));
    }

    [Fact]
    public void Meals_outside_the_window_form_no_series()
    {
        // The meal date filter is the same AND; the same ghost-series test, on the recipe split.
        var result = ReportEngine.Run(Spec(ReportMetric.MealsCooked, ReportSplit.ByRecipe), [],
            [Meal(15), Meal(15, month: 5, recipeId: 2, name: "Before"), Meal(15, month: 8, recipeId: 3, name: "After")]);

        Assert.Equal(["Tacos"], result.Series.Select(s => s.Label));
    }

    [Fact]
    public void A_window_ending_on_a_bucket_start_still_gets_that_bucket()
    {
        // To = Aug 1 is exactly August's bucket start: the loop's boundary is inclusive, so August
        // exists (and an Aug 1 purchase lands in it instead of vanishing off the axis).
        var result = ReportEngine.Run(
            Spec() with { To = new DateOnly(2026, 8, 1) }, [Buy(1, month: 8)], []);

        Assert.Equal(["Jun", "Jul", "Aug"], result.Buckets.Select(b => b.Label));
        Assert.Equal(3.50m, result.Series.Single().Values[2]);
    }

    [Fact]
    public void Quarter_labels_number_the_calendar_quarters()
    {
        var result = ReportEngine.Run(
            Spec() with { Grain = ReportGrain.Quarterly, From = new DateOnly(2026, 1, 1), To = new DateOnly(2026, 12, 31) },
            [], []);

        Assert.Equal(["Q1", "Q2", "Q3", "Q4"], result.Buckets.Select(b => b.Label));
    }

    // ---- Compare-previous: the mirrored window's exact arithmetic --------------------------------

    [Fact]
    public void The_previous_window_is_the_same_length_ending_the_day_before_From()
    {
        // June (30 days) compares against May 2 – May 31: same length, ending the day before June
        // starts. A fact on May 2 is the previous window's FIRST day (an off-by-one shorter window
        // loses it); a fact on Jun 1 belongs to the CURRENT window only (a To that leaned forward
        // would double-count it into both).
        var spec = Spec() with { To = new DateOnly(2026, 6, 30), ComparePrevious = true };

        var result = ReportEngine.Run(spec, [Buy(2, month: 5), Buy(1)], []);

        Assert.Equal(3.50m, result.Total);
        Assert.Equal(3.50m, result.PreviousTotal);
    }

    // ---- Disclosure notes: the exact sentences --------------------------------------------------

    [Fact]
    public void The_unpriced_note_counts_and_conjugates()
    {
        var one = ReportEngine.Run(Spec(), [Buy(5), Buy(6, price: null)], []);
        Assert.Equal("1 unpriced purchase is not in the spend.", one.Note);

        var two = ReportEngine.Run(Spec(), [Buy(5), Buy(6, price: null), Buy(7, price: null)], []);
        Assert.Equal("2 unpriced purchases are not in the spend.", two.Note);

        Assert.Null(ReportEngine.Run(Spec(), [Buy(5)], []).Note); // nothing unpriced → no note
    }

    [Fact]
    public void The_calorie_note_counts_only_the_unknowns_and_conjugates()
    {
        // Two unknowns beside one known: the count must be of the NULLS (2), not the knowns (1) —
        // a flipped predicate reads "1 meal has" here and the sentence lies by omission.
        var meals = new[] { Meal(5), Meal(6, kcal: null), Meal(7, kcal: null) };
        var two = ReportEngine.Run(Spec(ReportMetric.Calories), [], meals);
        Assert.Equal("2 meals have no calorie estimate and aren't counted.", two.Note);

        var one = ReportEngine.Run(Spec(ReportMetric.Calories), [], [Meal(5), Meal(6, kcal: null)]);
        Assert.Equal("1 meal has no calorie estimate and aren't counted.", one.Note);

        Assert.Null(ReportEngine.Run(Spec(ReportMetric.Calories), [], [Meal(5)]).Note);
    }

    [Fact]
    public void The_topN_disclosure_conjugates_and_names_the_split()
    {
        // Tag series never pool (overlap), so beyond-TopN tags are DISCLOSED. Singular trims the
        // noun ("tag", "isn't"); plural keeps it. The exact sentence is the user-facing honesty
        // claim — "N more X aren't shown" — and both halves must agree with the count.
        PurchaseFact Tagged(int day, string tag, decimal price) => Buy(day, price: price, tags: [tag]);
        var spec = Spec(split: ReportSplit.ByTag) with { TopN = 2 };

        var one = ReportEngine.Run(spec, [Tagged(1, "a", 9), Tagged(2, "b", 8), Tagged(3, "c", 1)], []);
        Assert.Equal("1 more tag isn't shown (below the top 2).", one.Note);

        var two = ReportEngine.Run(spec,
            [Tagged(1, "a", 9), Tagged(2, "b", 8), Tagged(3, "c", 1), Tagged(4, "d", 1)], []);
        Assert.Equal("2 more tags aren't shown (below the top 2).", two.Note);
    }

    [Fact]
    public void Quantity_by_category_is_refused_because_units_differ_across_categories()
    {
        // Quantity needs one product (or a by-product split). A by-CATEGORY split still sums mixed
        // units across products (9 milks + 1 bread + 1 soap isn't 11 of anything), so ByCategory does
        // NOT satisfy the rule's "or split by product" escape and the spec is refused — the engine
        // never even reaches the disclose/pool step. (The valid case, where quantity DOES disclose
        // its remainder rather than pool, is Quantity_split_by_product_neither_pools_nor_totals_but_discloses.)
        var spec = Spec(ReportMetric.Quantity, ReportSplit.ByCategory) with { TopN = 1 };

        Assert.NotEmpty(ReportSpecRules.Check(spec));
        Assert.Throws<ArgumentException>(() => ReportEngine.Run(spec, [], []));
    }

    [Fact]
    public void Two_notes_join_into_one_sentence_stream()
    {
        // Both disclosures at once: the Note is the sentences joined with a space — a UI renders one
        // paragraph, not a mashed "spend.2 more".
        var spec = Spec(split: ReportSplit.ByTag) with { TopN = 1 };
        var result = ReportEngine.Run(spec,
            [Buy(1, price: null, tags: ["a"]), Buy(2, price: 9, tags: ["b"]), Buy(3, price: 1, tags: ["c"])], []);

        Assert.Equal(
            "1 unpriced purchase is not in the spend. 2 more tags aren't shown (below the top 1).",
            result.Note);
    }

    // ---- Series labels and flags ----------------------------------------------------------------

    [Fact]
    public void The_single_series_label_names_the_filter_it_wears()
    {
        var facts = new[] { Buy(5, tags: ["snacks"]) };

        Assert.Equal("Whole Milk",
            ReportEngine.Run(Spec() with { ProductId = 1 }, facts, []).Series.Single().Label);
        Assert.Equal("snacks",
            ReportEngine.Run(Spec() with { Tag = "snacks" }, facts, []).Series.Single().Label);
        Assert.Equal("All items", ReportEngine.Run(Spec(), facts, []).Series.Single().Label);
    }

    [Fact]
    public void A_product_filter_matching_nothing_labels_safely()
    {
        // ProductId set but no facts survived the filters: there is no facts[0] to name the series
        // after, and reaching for it would throw — the label falls back instead.
        var result = ReportEngine.Run(Spec() with { ProductId = 99 }, [Buy(5)], []);

        Assert.Equal("All items", result.Series.Single().Label);
    }

    [Fact]
    public void Meal_series_without_a_split_stay_one_series_named_for_the_metric()
    {
        // Two distinct recipes, NO split: one series, named "Meals" (or "Calories" for that metric)
        // — an accidental by-recipe grouping here would leak a split nobody asked for.
        var meals = new[] { Meal(5), Meal(6, recipeId: 2, name: "Stew") };

        var count = ReportEngine.Run(Spec(ReportMetric.MealsCooked), [], meals);
        Assert.Equal("Meals", count.Series.Single().Label);

        var kcal = ReportEngine.Run(Spec(ReportMetric.Calories), [], meals);
        Assert.Equal("Calories", kcal.Series.Single().Label);
    }

    [Fact]
    public void A_single_series_is_never_called_stackable()
    {
        // Stackable is the chart's permission to stack: one series has nothing to stack with, even
        // under a split that WOULD stack were there more (one category bought → one series).
        var result = ReportEngine.Run(Spec(split: ReportSplit.ByCategory), [Buy(5)], []);

        Assert.False(result.Stackable);
    }

    [Fact]
    public void A_bucket_with_facts_but_no_paid_prices_is_a_gap_not_a_crash()
    {
        // UnitPrice averages PAID prices only. A bucket whose facts are all price-index estimates
        // has an empty paid list — that's the documented gap (null), and averaging it would throw.
        var spec = Spec(ReportMetric.UnitPrice) with { ProductId = 1 };

        var result = ReportEngine.Run(spec, [Buy(5, estimateOnly: true)], []);

        Assert.Null(result.Series.Single().Values[0]);
    }
}
