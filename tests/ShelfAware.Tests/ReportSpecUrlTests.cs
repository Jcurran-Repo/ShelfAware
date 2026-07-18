using ShelfAware.Core.Domain;
using ShelfAware.Core.Reporting;

namespace ShelfAware.Tests;

public class ReportSpecUrlTests
{
    private static readonly DateOnly From = new(2026, 6, 1);
    private static readonly DateOnly To = new(2026, 7, 18);

    private static ReportSpec Parse(string query)
    {
        var values = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => (string?)Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
        return ReportSpecUrl.FromQuery(values, From, To);
    }

    [Fact]
    public void Round_trips_a_fully_specified_spec()
    {
        var spec = new ReportSpec
        {
            Metric = ReportMetric.Quantity,
            Grain = ReportGrain.Weekly,
            From = From,
            To = To,
            Split = ReportSplit.ByProduct,
            TopN = 4,
            Category = Category.Dairy,
            ProductId = 12,
            Tag = "kids snacks", // spaces must survive the trip
            Chart = ReportChart.Bars,
            ComparePrevious = true,
        };

        Assert.Equal(spec, Parse(ReportSpecUrl.ToQuery(spec)));
    }

    [Fact]
    public void Defaults_are_omitted_so_urls_stay_short()
    {
        var query = ReportSpecUrl.ToQuery(new ReportSpec { From = From, To = To });
        Assert.Equal("from=2026-06-01&to=2026-07-18", query);
    }

    [Fact]
    public void Garbage_degrades_to_defaults_never_throws()
    {
        // Typed URLs, links from old versions, mangled shares — the page must still render.
        var spec = Parse("metric=Nonsense&grain=Fortnightly&from=yesterday&to=2026-99-99&top=999&product=abc&chart=PieChart");

        Assert.Equal(ReportMetric.Spend, spec.Metric);
        Assert.Equal(ReportGrain.Monthly, spec.Grain);
        Assert.Equal(From, spec.From);   // fallback window, not a crash
        Assert.Equal(To, spec.To);
        Assert.Equal(6, spec.TopN);      // out-of-range clamps to default
        Assert.Null(spec.ProductId);
        Assert.Equal(ReportChart.Line, spec.Chart);
    }

    [Fact]
    public void Enum_parsing_is_case_insensitive_but_refuses_numeric_smuggling()
    {
        Assert.Equal(ReportSplit.ByTag, Parse("split=bytag").Split);
        // Enum.TryParse accepts raw integers; an undefined one must not become a phantom enum value.
        Assert.Equal(ReportSplit.None, Parse("split=99").Split);
    }
}
