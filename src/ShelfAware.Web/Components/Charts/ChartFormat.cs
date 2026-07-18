using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using ShelfAware.Core.Reporting;

namespace ShelfAware.Web.Components.Charts;

/// <summary>How report numbers and series colors render — one home so the charts, legends, tables,
/// and tooltips can't drift apart on formatting or slot assignment.</summary>
public static class ChartFormat
{
    /// <summary>A full-precision value for tooltips and table cells.</summary>
    public static string Value(ReportUnit unit, decimal value) => unit switch
    {
        ReportUnit.Money => value.ToString("C", CultureInfo.CurrentCulture),
        ReportUnit.Calories => $"{value:N0} kcal",
        ReportUnit.Quantity => value.ToString("0.##", CultureInfo.CurrentCulture),
        _ => value.ToString("N0", CultureInfo.CurrentCulture),
    };

    /// <summary>An axis-tick value — whole dollars when the scale allows, so the axis stays quiet.</summary>
    public static string Tick(ReportUnit unit, decimal value) => unit switch
    {
        ReportUnit.Money when value == decimal.Truncate(value) => value.ToString("C0", CultureInfo.CurrentCulture),
        ReportUnit.Money => value.ToString("C", CultureInfo.CurrentCulture),
        ReportUnit.Quantity => value.ToString("0.##", CultureInfo.CurrentCulture),
        _ => value.ToString("N0", CultureInfo.CurrentCulture),
    };

    /// <summary>The series' color slot. Slots are assigned in rank order and NEVER cycled — the
    /// engine's top-N cap keeps series counts inside the eight validated slots — and the pooled
    /// "everything else"/(untagged) series wear a recessive gray so they read as background, not as
    /// a ninth competitor.</summary>
    public static string SeriesColor(ReportSeries series, int index) =>
        IsPooled(series) ? "var(--chart-pooled)" : $"var(--chart-{Math.Min(index + 1, 8)})";

    private static bool IsPooled(ReportSeries series) =>
        series.Label is "Everything else" or "(untagged)";

    /// <summary>An SVG &lt;text&gt; element as raw markup. Razor reserves the lowercase &lt;text&gt;
    /// tag for its own literal syntax (RZ1023), so axis labels can't be written inline — they're
    /// built here, with the content HTML-encoded because MarkupString bypasses Razor's encoding.</summary>
    public static MarkupString SvgText(double x, double y, string anchor, string content) =>
        new($"<text x=\"{Inv(x)}\" y=\"{Inv(y)}\" text-anchor=\"{anchor}\" " +
            $"class=\"report-chart-tick\">{WebUtility.HtmlEncode(content)}</text>");

    private static string Inv(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
