using ShelfAware.Web.Components;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The hand-rolled price-history SVG. The math IS the component: x spaced by index, y inverted
/// (SVG grows downward, prices grow upward) inside the padding, a flat series held at mid-height
/// rather than divided by zero.
/// </summary>
public class LineChartTests : PageTestContext
{
    [Fact]
    public void Two_points_span_the_width_with_y_inverted()
    {
        var cut = Render<LineChart>(ps => ps
            .Add(p => p.Values, new List<decimal> { 1m, 3m })
            .Add(p => p.W, 100d)
            .Add(p => p.H, 36d));

        // min (1) sits at the BOTTOM of the padded plot (y=32), max (3) at the top (y=4) — a
        // chart that drew rising prices downward would be lying with correct numbers.
        Assert.Equal("0,32 100,4", cut.Find("polyline").GetAttribute("points"));
    }

    [Fact]
    public void A_single_point_draws_no_line_but_can_still_dot_the_middle()
    {
        var cut = Render<LineChart>(ps => ps
            .Add(p => p.Values, new List<decimal> { 2.5m })
            .Add(p => p.W, 100d)
            .Add(p => p.H, 36d)
            .Add(p => p.ShowDots, true));

        Assert.Empty(cut.FindAll("polyline")); // one point is not a trend
        var dot = Assert.Single(cut.FindAll("circle"));
        Assert.Equal("50", dot.GetAttribute("cx")); // centered, not glued to an edge
    }

    [Fact]
    public void A_flat_series_holds_the_middle_instead_of_dividing_by_zero()
    {
        var cut = Render<LineChart>(ps => ps
            .Add(p => p.Values, new List<decimal> { 2m, 2m, 2m })
            .Add(p => p.W, 100d)
            .Add(p => p.H, 36d));

        // range == 0 → every y at Pad + half the plot height.
        Assert.Equal("0,18 50,18 100,18", cut.Find("polyline").GetAttribute("points"));
    }

    [Fact]
    public void The_chart_is_labelled_for_a_screen_reader()
    {
        var cut = Render<LineChart>(ps => ps
            .Add(p => p.Values, new List<decimal> { 1m, 2m })
            .Add(p => p.AriaLabel, "Whole Milk price per purchase over time"));

        var svg = cut.Find("svg");
        Assert.Equal("img", svg.GetAttribute("role"));
        Assert.Equal("Whole Milk price per purchase over time", svg.GetAttribute("aria-label"));
    }
}
