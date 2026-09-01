using ShelfAware.Web.Components;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The sortable header's accessibility contract: aria-sort and the arrow indicator track the active column
/// and direction through the click cycle (none → ascending → descending → off), so a screen reader is told
/// the same thing the arrow shows — never colour/glyph alone.
/// </summary>
public class SortableHeaderTests : PageTestContext
{
    private IRenderedComponent<SortableHeader> RenderHeader(TableSort sort) =>
        Render<SortableHeader>(ps => ps
            .Add(p => p.Column, "count")
            .Add(p => p.Label, "Count")
            .Add(p => p.Sort, sort)
            .Add(p => p.OnSort, (string c) => sort.Toggle(c)));

    [Fact]
    public void Aria_sort_and_the_arrow_cycle_none_ascending_descending_off()
    {
        var sort = new TableSort();
        var cut = RenderHeader(sort);

        Assert.Equal("none", cut.Find("th").GetAttribute("aria-sort")); // inactive to start
        Assert.Empty(cut.Find(".sort-ind").TextContent.Trim());

        cut.Find("button.th-sort").Click();                             // → ascending
        Assert.Equal("ascending", cut.Find("th").GetAttribute("aria-sort"));
        Assert.Contains("▲", cut.Find(".sort-ind").TextContent);
        Assert.Contains("sorted", cut.Find("th").GetAttribute("class") ?? "");

        cut.Find("button.th-sort").Click();                             // → descending
        Assert.Equal("descending", cut.Find("th").GetAttribute("aria-sort"));
        Assert.Contains("▼", cut.Find(".sort-ind").TextContent);

        cut.Find("button.th-sort").Click();                             // → off (natural order)
        Assert.Equal("none", cut.Find("th").GetAttribute("aria-sort"));
        Assert.Empty(cut.Find(".sort-ind").TextContent.Trim());
    }

    [Fact]
    public void A_header_for_another_column_is_not_marked_active()
    {
        var sort = new TableSort();
        sort.Toggle("something-else"); // a DIFFERENT column is the active sort

        var cut = RenderHeader(sort);

        Assert.Equal("none", cut.Find("th").GetAttribute("aria-sort"));
        Assert.DoesNotContain("sorted", cut.Find("th").GetAttribute("class") ?? "");
    }
}
