using System.Globalization;
using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Reporting;

/// <summary>
/// A <see cref="ReportSpec"/> as a query string and back — THE serialization for specs. It's what
/// makes every configured report a bookmarkable URL, it's the format saved reports store (one
/// parser to maintain, and a saved row is human-readable in the DB), and it's the surface a chat
/// tool can navigate to. Defaults are omitted so URLs stay short; parsing is forgiving because
/// URLs are typed, shared, and saved by old versions — a value that doesn't parse falls back to
/// the default rather than failing the page.
/// </summary>
public static class ReportSpecUrl
{
    private static readonly ReportSpec Defaults = new();

    /// <summary>The spec as query-string pairs (no leading "?"), omitting anything at its default.
    /// From/To are always emitted — a report's window is its identity.</summary>
    public static string ToQuery(ReportSpec spec)
    {
        var parts = new List<string>
        {
            $"from={spec.From:yyyy-MM-dd}",
            $"to={spec.To:yyyy-MM-dd}",
        };
        if (spec.Metric != Defaults.Metric) parts.Add($"metric={spec.Metric}");
        if (spec.Grain != Defaults.Grain) parts.Add($"grain={spec.Grain}");
        if (spec.Split != Defaults.Split) parts.Add($"split={spec.Split}");
        if (spec.TopN != Defaults.TopN) parts.Add($"top={spec.TopN}");
        if (spec.Category is { } category) parts.Add($"category={category}");
        if (spec.ProductId is { } productId) parts.Add($"product={productId}");
        if (spec.Tag is { } tag) parts.Add($"tag={Uri.EscapeDataString(tag)}");
        if (spec.RecipeId is { } recipeId) parts.Add($"recipe={recipeId}");
        if (spec.Chart != Defaults.Chart) parts.Add($"chart={spec.Chart}");
        if (spec.ComparePrevious) parts.Add("compare=1");
        return string.Join('&', parts);
    }

    /// <summary>Rebuild a spec from parsed query values (each key's first value). Anything absent
    /// or unparseable takes the default; there is deliberately no error path — the worst a mangled
    /// URL can do is show a default report, and the spec rules still gate what actually runs.</summary>
    public static ReportSpec FromQuery(IReadOnlyDictionary<string, string?> values, DateOnly fallbackFrom, DateOnly fallbackTo)
    {
        return new ReportSpec
        {
            From = Date("from") ?? fallbackFrom,
            To = Date("to") ?? fallbackTo,
            Metric = Enum<ReportMetric>("metric") ?? Defaults.Metric,
            Grain = Enum<ReportGrain>("grain") ?? Defaults.Grain,
            Split = Enum<ReportSplit>("split") ?? Defaults.Split,
            // A sanity bound only — whether a value over the chart color cap is legal depends on the
            // chart kind, and that's ReportSpecRules' call, not the parser's (a saved table report
            // with top=10 must survive the round trip).
            TopN = Int("top") is { } top and >= 1 and <= 50 ? top : Defaults.TopN,
            Category = Enum<Category>("category"),
            ProductId = Int("product"),
            Tag = Text("tag"),
            RecipeId = Int("recipe"),
            Chart = Enum<ReportChart>("chart") ?? Defaults.Chart,
            ComparePrevious = Text("compare") == "1",
        };

        string? Text(string key) =>
            values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        DateOnly? Date(string key) =>
            DateOnly.TryParseExact(Text(key), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d : null;

        int? Int(string key) =>
            int.TryParse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

        TEnum? Enum<TEnum>(string key) where TEnum : struct, System.Enum =>
            System.Enum.TryParse<TEnum>(Text(key), ignoreCase: true, out var e)
                && System.Enum.IsDefined(e) ? e : null;
    }
}
