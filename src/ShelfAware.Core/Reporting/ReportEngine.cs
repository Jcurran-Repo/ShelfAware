using System.Globalization;

namespace ShelfAware.Core.Reporting;

/// <summary>What kind of number the values are — the UI formats from this ($, plain count, kcal),
/// the engine never renders strings it would then have to parse back.</summary>
public enum ReportUnit { Money, Count, Quantity, Calories }

/// <summary>
/// Turns a <see cref="ReportSpec"/> plus flat facts into chart-ready series. Pure plain code — every
/// number a report shows is deterministic arithmetic over receipt-derived facts; the LLMs' job ended
/// upstream at extraction. Zero-fills the time axis, enforces the spec rules (a UI bug must not
/// produce a lying chart), pools or discloses whatever top-N cuts, and discloses skipped rows.
/// </summary>
public static class ReportEngine
{
    private const string UntaggedLabel = "(untagged)";
    private const string PooledLabel = "Everything else";

    public static ReportResult Run(
        ReportSpec spec,
        IReadOnlyList<PurchaseFact> purchases,
        IReadOnlyList<MealFact> meals)
    {
        var problems = ReportSpecRules.Check(spec);
        if (problems.Count > 0)
            throw new ArgumentException($"The report spec is not sound: {string.Join(" ", problems)}", nameof(spec));

        var buckets = BuildBuckets(spec.From, spec.To, spec.Grain);
        var notes = new List<string>();

        var series = spec.IsMealMetric
            ? MealSeries(spec, meals, buckets, notes)
            : PurchaseSeries(spec, purchases, buckets, notes);

        var stackable = ReportSpecRules.Stackable(spec) && series.Count > 1;
        var additive = spec.Metric != ReportMetric.UnitPrice;
        // A grand total is only claimed when the series partition the filtered facts: a single
        // series trivially does; a stackable split does; overlapping tags and mixed units never do.
        var totalIsHonest = additive && (series.Count <= 1 || ReportSpecRules.Stackable(spec));
        var total = totalIsHonest ? series.Sum(s => s.Total) : (decimal?)null;

        decimal? previousTotal = null;
        if (spec.ComparePrevious && totalIsHonest)
        {
            var length = spec.To.DayNumber - spec.From.DayNumber + 1;
            var previous = spec with
            {
                From = spec.From.AddDays(-length),
                To = spec.From.AddDays(-1),
                ComparePrevious = false,
            };
            previousTotal = Run(previous, purchases, meals).Total;
        }

        return new ReportResult
        {
            Buckets = buckets,
            Series = series,
            Stackable = stackable,
            Additive = additive,
            Total = total,
            PreviousTotal = previousTotal,
            Note = notes.Count > 0 ? string.Join(" ", notes) : null,
            Unit = UnitOf(spec.Metric),
        };
    }

    public static ReportUnit UnitOf(ReportMetric metric) => metric switch
    {
        ReportMetric.Spend or ReportMetric.UnitPrice => ReportUnit.Money,
        ReportMetric.Quantity => ReportUnit.Quantity,
        ReportMetric.Calories => ReportUnit.Calories,
        _ => ReportUnit.Count,
    };

    // ---- Purchases ------------------------------------------------------------------------------

    private static List<ReportSeries> PurchaseSeries(
        ReportSpec spec, IReadOnlyList<PurchaseFact> purchases, List<ReportBucket> buckets, List<string> notes)
    {
        var facts = purchases
            .Where(f => f.Date >= spec.From && f.Date <= spec.To)
            .Where(f => spec.Category is null || f.Category == spec.Category)
            .Where(f => spec.ProductId is null || f.ProductId == spec.ProductId)
            .Where(f => spec.Tag is null || f.Tags.Contains(spec.Tag, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (spec.Metric == ReportMetric.Spend)
        {
            var unpriced = facts.Count(f => f.Price is null);
            if (unpriced > 0)
                notes.Add($"{unpriced} unpriced {(unpriced == 1 ? "purchase is" : "purchases are")} not in the spend.");
        }

        List<(string Label, List<PurchaseFact> Facts)> groups = spec.Split switch
        {
            ReportSplit.ByCategory => facts
                .GroupBy(f => f.Category)
                .Select(g => (Label: g.Key.ToString(), Facts: g.ToList()))
                .ToList(),
            ReportSplit.ByProduct => facts
                .GroupBy(f => f.ProductId)
                .Select(g => (Label: g.First().ProductName, Facts: g.ToList()))
                .ToList(),
            ReportSplit.ByTag => TagGroups(facts),
            _ => [(Label: SingleSeriesLabel(spec, facts), Facts: facts)],
        };

        return Assemble(spec, buckets, notes, groups,
            valueOf: (bucketFacts) => PurchaseValue(spec.Metric, bucketFacts),
            dateOf: f => f.Date,
            // Pooling the remainder is only honest when the pooled values share units and partition:
            // spend/count by product or category pool fine (and a partitioning split MUST pool —
            // dropping small categories from a stacked chart would falsify the stack's total);
            // quantities never pool (mixed units); tag series can't pool at all (overlap).
            poolRemainder: spec.Split is ReportSplit.ByProduct or ReportSplit.ByCategory
                && spec.Metric != ReportMetric.Quantity,
            remainderNoun: spec.Split switch
            {
                ReportSplit.ByTag => "tags",
                ReportSplit.ByCategory => "categories",
                _ => "products",
            });
    }

    /// <summary>One series per tag, top-N by total later. A purchase carrying two tags appears in
    /// both series — the overlap is the feature (compare "snacks" against "kids" even though goldfish
    /// crackers are both), and the reason tag series never stack or total.</summary>
    private static List<(string Label, List<PurchaseFact> Facts)> TagGroups(List<PurchaseFact> facts)
    {
        var byTag = new Dictionary<string, List<PurchaseFact>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in facts)
        {
            if (fact.Tags.Count == 0)
            {
                Bucket(UntaggedLabel).Add(fact);
                continue;
            }
            foreach (var tag in fact.Tags) Bucket(tag).Add(fact);

            List<PurchaseFact> Bucket(string key) =>
                byTag.TryGetValue(key, out var list) ? list : byTag[key] = [];
        }
        return byTag.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static decimal? PurchaseValue(ReportMetric metric, IReadOnlyList<PurchaseFact> bucketFacts)
    {
        switch (metric)
        {
            case ReportMetric.Spend:
                return bucketFacts.Sum(f => (f.Price ?? 0) * f.Quantity);
            case ReportMetric.Quantity:
                return bucketFacts.Sum(f => f.Quantity);
            case ReportMetric.PurchaseCount:
                return bucketFacts.Count;
            case ReportMetric.UnitPrice:
                var paid = bucketFacts
                    .Where(f => f.InDominantSizeBucket && f.PaidUnitPrice is not null)
                    .Select(f => f.PaidUnitPrice!.Value)
                    .ToList();
                return paid.Count > 0 ? Math.Round(paid.Average(), 2) : null;
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, "Not a purchase metric.");
        }
    }

    private static string SingleSeriesLabel(ReportSpec spec, List<PurchaseFact> facts) =>
        spec.ProductId is not null && facts.Count > 0 ? facts[0].ProductName
        : spec.Tag is not null ? spec.Tag
        : spec.Category is { } category ? category.ToString()
        : "All items";

    // ---- Meals ----------------------------------------------------------------------------------

    private static List<ReportSeries> MealSeries(
        ReportSpec spec, IReadOnlyList<MealFact> meals, List<ReportBucket> buckets, List<string> notes)
    {
        var facts = meals
            .Where(m => m.Date >= spec.From && m.Date <= spec.To)
            .Where(m => spec.RecipeId is null || m.RecipeId == spec.RecipeId)
            .ToList();

        if (spec.Metric == ReportMetric.Calories)
        {
            var unknown = facts.Count(m => m.CaloriesPerServing is null);
            if (unknown > 0)
                notes.Add($"{unknown} {(unknown == 1 ? "meal has" : "meals have")} no calorie estimate and aren't counted.");
        }

        List<(string Label, List<MealFact> Facts)> groups = spec.Split == ReportSplit.ByRecipe
            ? facts.GroupBy(m => m.RecipeId).Select(g => (Label: g.First().RecipeName, Facts: g.ToList())).ToList()
            : [(Label: spec.Metric == ReportMetric.Calories ? "Calories" : "Meals", Facts: facts)];

        return Assemble(spec, buckets, notes, groups,
            valueOf: bucketFacts => spec.Metric == ReportMetric.Calories
                ? bucketFacts.Sum(m => (decimal)(m.CaloriesPerServing ?? 0))
                : bucketFacts.Count,
            dateOf: m => m.Date,
            poolRemainder: spec.Split == ReportSplit.ByRecipe,
            remainderNoun: "recipes");
    }

    // ---- Shared assembly ------------------------------------------------------------------------

    /// <summary>Ranks groups by window total, keeps the top N (pooling or disclosing the rest), and
    /// walks each survivor across the buckets. Shared by both fact kinds so top-N/pooling/zero-fill
    /// behave identically everywhere.</summary>
    private static List<ReportSeries> Assemble<TFact>(
        ReportSpec spec,
        List<ReportBucket> buckets,
        List<string> notes,
        List<(string Label, List<TFact> Facts)> groups,
        Func<IReadOnlyList<TFact>, decimal?> valueOf,
        Func<TFact, DateOnly> dateOf,
        bool poolRemainder,
        string remainderNoun)
    {
        List<decimal?> Walk(List<TFact> facts)
        {
            var byBucket = facts
                .GroupBy(f => BucketStart(dateOf(f), spec.Grain))
                .ToDictionary(g => g.Key, g => g.ToList());
            return buckets
                .Select(b => byBucket.TryGetValue(b.Start, out var inBucket)
                    ? valueOf(inBucket)
                    // An empty bucket is genuinely zero for additive metrics (nothing was bought);
                    // for an average it's the absence of data, and the chart draws the gap.
                    : spec.Metric == ReportMetric.UnitPrice ? null : 0m)
                .ToList();
        }

        if (spec.Split == ReportSplit.None || groups.Count <= 1)
            return groups.Select(g => new ReportSeries(g.Label, Walk(g.Facts))).ToList();

        var ranked = groups
            .Select(g => (g.Label, g.Facts, Total: g.Facts.Sum(f => WeightOf(f))))
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Ranking weight: the same number the series will chart, so "top by spend" really is by spend.
        decimal WeightOf(TFact f) => valueOf([f]) ?? 0;

        var kept = ranked.Take(spec.TopN).ToList();
        var rest = ranked.Skip(spec.TopN).ToList();

        var series = kept.Select(g => new ReportSeries(g.Label, Walk(g.Facts))).ToList();
        if (rest.Count > 0)
        {
            if (poolRemainder)
            {
                series.Add(new ReportSeries(PooledLabel, Walk(rest.SelectMany(g => g.Facts).ToList())));
            }
            else
            {
                var one = rest.Count == 1;
                notes.Add($"{rest.Count} more {(one ? remainderNoun[..^1] : remainderNoun)} " +
                          $"{(one ? "isn't" : "aren't")} shown (below the top {spec.TopN}).");
            }
        }
        return series;
    }

    // ---- Time buckets ---------------------------------------------------------------------------

    private static List<ReportBucket> BuildBuckets(DateOnly from, DateOnly to, ReportGrain grain)
    {
        var buckets = new List<ReportBucket>();
        var multiYear = BucketStart(from, grain).Year != BucketStart(to, grain).Year;
        for (var start = BucketStart(from, grain); start <= to; start = NextBucket(start, grain))
        {
            buckets.Add(new ReportBucket(start, Label(start, grain, multiYear)));
        }
        return buckets;
    }

    /// <summary>The calendar period a date belongs to: Monday-started weeks, first-of-month months,
    /// first-of-quarter quarters. Calendar periods (not offsets from From) so "June" means June.</summary>
    public static DateOnly BucketStart(DateOnly date, ReportGrain grain) => grain switch
    {
        ReportGrain.Weekly => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        ReportGrain.Monthly => new DateOnly(date.Year, date.Month, 1),
        ReportGrain.Quarterly => new DateOnly(date.Year, (date.Month - 1) / 3 * 3 + 1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(grain), grain, null),
    };

    private static DateOnly NextBucket(DateOnly start, ReportGrain grain) => grain switch
    {
        ReportGrain.Weekly => start.AddDays(7),
        ReportGrain.Monthly => start.AddMonths(1),
        _ => start.AddMonths(3),
    };

    private static string Label(DateOnly start, ReportGrain grain, bool withYear)
    {
        var label = grain switch
        {
            ReportGrain.Weekly => start.ToString("MMM d", CultureInfo.InvariantCulture),
            ReportGrain.Monthly => start.ToString("MMM", CultureInfo.InvariantCulture),
            _ => $"Q{(start.Month - 1) / 3 + 1}",
        };
        return withYear ? $"{label} '{start.Year % 100:00}" : label;
    }
}
