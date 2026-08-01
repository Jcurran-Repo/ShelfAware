using ShelfAware.Web.Components;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The shared usual-brand/variety chips (grocery list, products grid, dashboard cards). A native
/// details element on purpose — the breakdown has to open on a TAP in a store aisle, and a
/// floating popover would be clipped by the grids' scroll containers.
/// </summary>
public class BrandVarietyHintTests : PageTestContext
{
    [Fact]
    public void Nothing_renders_when_there_is_no_usual_to_show()
    {
        // Lists alone don't earn a chip: with no usual brand OR variety, the component must not
        // render an empty summary that opens onto a breakdown nobody asked about.
        var cut = Render<BrandVarietyHint>(ps => ps
            .Add(p => p.Brands, ["Great Value", "Sara Lee"]));

        Assert.Equal("", cut.Markup.Trim());
    }

    [Fact]
    public void The_summary_carries_the_usuals_and_the_breakdown_lists_every_kind_bought()
    {
        var cut = Render<BrandVarietyHint>(ps => ps
            .Add(p => p.UsualBrand, "Kool-Aid")
            .Add(p => p.UsualVariety, "Strawberry")
            .Add(p => p.Brands, ["Crystal Light", "Kool-Aid"])
            .Add(p => p.Varieties, ["Grape", "Strawberry"]));

        Assert.Equal("Kool-Aid", cut.Find(".usual-brand").TextContent);
        Assert.Equal("Strawberry", cut.Find(".usual-variety").TextContent);
        Assert.Contains("Crystal Light · Kool-Aid", cut.Find(".hint-pop-body").TextContent);
        Assert.Contains("Grape · Strawberry", cut.Find(".hint-pop-body").TextContent);
    }

    [Fact]
    public void An_empty_breakdown_row_is_omitted_not_rendered_blank()
    {
        var cut = Render<BrandVarietyHint>(ps => ps
            .Add(p => p.UsualVariety, "Strawberry")
            .Add(p => p.Varieties, ["Grape", "Strawberry"]));

        var body = cut.Find(".hint-pop-body").TextContent;
        Assert.DoesNotContain("Brands", body);
        Assert.Contains("Varieties", body);
    }
}
