namespace ShelfAware.Core.Reporting;

using ShelfAware.Core.Domain;

public enum ReportMetric
{
    /// <summary>Dollars spent — priced from receipts (paid line price, index estimate as fallback).
    /// The only metric that is additive ACROSS products, which is why it's the default.</summary>
    Spend,
    /// <summary>What one unit cost over time. Per-product only, and only the dominant size bucket's
    /// paid prices — the same like-with-like rule the Trends tickers follow ($/bag never averages
    /// against $/lime).</summary>
    UnitPrice,
    /// <summary>How many were bought. Units differ per product (3 lb of beef + 2 limes = nothing),
    /// so quantity NEVER sums across products — per-product only, or split by product.</summary>
    Quantity,
    /// <summary>How many purchase events. Additive everywhere (a count is unitless).</summary>
    PurchaseCount,
    /// <summary>Meals cooked, from the "Ate it" log. Dated events only — history from before the
    /// meal log existed has no dates and is not invented.</summary>
    MealsCooked,
    /// <summary>Calories cooked: meals × the recipe's estimated calories per serving. A ballpark of
    /// a ballpark (the estimate is LLM-guessed and per-serving, not per-pot) — the UI says so.</summary>
    Calories,
}

public enum ReportGrain { Weekly, Monthly, Quarterly }

public enum ReportSplit
{
    None,
    /// <summary>One series per store-aisle category. Categories partition purchases, so this split
    /// may stack to an honest total.</summary>
    ByCategory,
    /// <summary>One series per product (top N by the window's total, remainder pooled where honest).</summary>
    ByProduct,
    /// <summary>One series per tag. Tags OVERLAP (one product can carry several), so these series
    /// are per-tag views of the same purchases — never stacked, never totalled.</summary>
    ByTag,
    /// <summary>One series per recipe — the meal metrics' split.</summary>
    ByRecipe,
}

public enum ReportChart { Line, Bars, StackedBars, Table }

/// <summary>Everything that defines a report: what to measure, over which purchases, bucketed how.
/// A record so a configured report is a VALUE — savable as JSON, round-trippable through a URL,
/// comparable for "did the user change anything".</summary>
public sealed record ReportSpec
{
    public ReportMetric Metric { get; init; } = ReportMetric.Spend;
    public ReportGrain Grain { get; init; } = ReportGrain.Monthly;
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public ReportSplit Split { get; init; } = ReportSplit.None;
    /// <summary>How many series a ByProduct/ByTag/ByRecipe split keeps before pooling/omitting the
    /// rest — a chart with forty legends is a table wearing a costume.</summary>
    public int TopN { get; init; } = 6;

    // ---- Subject filters (null = everything). These narrow which purchases/meals are measured;
    // the split then fans the survivors out into series.
    public Category? Category { get; init; }
    public int? ProductId { get; init; }
    /// <summary>Filter to purchases of products carrying this tag — the "by tag instead of product"
    /// half of the feature. Combines with Split like any other filter.</summary>
    public string? Tag { get; init; }
    public int? RecipeId { get; init; }

    public ReportChart Chart { get; init; } = ReportChart.Line;
    /// <summary>Also compute the total for the equal-length window immediately before From — the
    /// "vs last period" stat. Additive metrics only (an average has no meaningful period total).</summary>
    public bool ComparePrevious { get; init; }

    public bool IsMealMetric => Metric is ReportMetric.MealsCooked or ReportMetric.Calories;
}

/// <summary>The honesty rules, in one askable place: the builder UI greys options with these reasons,
/// and <see cref="ReportEngine"/> refuses to run a spec that breaks them (a UI bug must not produce a
/// lying chart). Empty list = the spec is sound.</summary>
public static class ReportSpecRules
{
    /// <summary>The palette has exactly eight validated categorical slots; a series count past that
    /// would repeat colors and un-earn the colorblind-separation guarantee. TopN is capped here (the
    /// rule layer) so the engine, the URL parser, and the builder all agree on one number.</summary>
    public const int MaxTopN = 8;

    public static IReadOnlyList<string> Check(ReportSpec spec)
    {
        var problems = new List<string>();

        if (spec.To < spec.From)
            problems.Add("The date range ends before it starts.");

        if (spec.Metric == ReportMetric.Quantity && spec.ProductId is null && spec.Split != ReportSplit.ByProduct)
            problems.Add("Quantity only means something within one product (3 lb of beef + 2 limes isn't 5 of anything) — pick a product, or split by product.");

        if (spec.Metric == ReportMetric.UnitPrice && spec.ProductId is null)
            problems.Add("Unit price is per item — pick a product to see what one of it costs over time.");

        if (spec.Metric == ReportMetric.UnitPrice && spec.Split != ReportSplit.None)
            problems.Add("Unit price doesn't split — it already tracks a single product's dominant size.");

        if (spec.IsMealMetric && spec.Split is not (ReportSplit.None or ReportSplit.ByRecipe))
            problems.Add("Meal metrics come from the recipe log — split by recipe, or not at all.");

        if (!spec.IsMealMetric && spec.Split == ReportSplit.ByRecipe)
            problems.Add("Splitting by recipe only applies to the meal metrics.");

        if (spec.IsMealMetric && (spec.Category is not null || spec.ProductId is not null || spec.Tag is not null))
            problems.Add("Meal metrics come from the recipe log — product, category, and tag filters don't apply.");

        if (!spec.IsMealMetric && spec.RecipeId is not null)
            problems.Add("A recipe filter only applies to the meal metrics.");

        if (spec.Chart == ReportChart.StackedBars && !Stackable(spec))
            problems.Add(spec.Split == ReportSplit.ByTag
                ? "Tag series overlap (one product can carry several tags), so stacking them would double-count — compare them side by side instead."
                : "Only a split that partitions the data (by category, product, or recipe) can stack to an honest total.");

        if (spec.TopN < 1)
            problems.Add("Keep at least one series.");

        // The cap is about chart color slots, so it binds CHARTS: a table has no colors and may
        // list more (the report card's top-10 items table is exactly that).
        if (spec.TopN > MaxTopN && spec.Chart != ReportChart.Table)
            problems.Add($"A chart can keep at most {MaxTopN} series — there are exactly {MaxTopN} validated chart colors, and a ninth series would silently wear the eighth's. Switch to a table to list more.");

        return problems;
    }

    /// <summary>Whether this split's series partition the data (each fact in exactly one series) —
    /// the precondition for a stacked chart or a summed total being true.</summary>
    public static bool Stackable(ReportSpec spec) => spec.Split switch
    {
        ReportSplit.ByTag => false, // overlapping by design
        ReportSplit.ByProduct when spec.Metric == ReportMetric.Quantity => false, // units differ per series
        ReportSplit.None => false,  // nothing to stack
        _ => true,
    };
}
