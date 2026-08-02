using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Products grid: the duplicate guard in front of the add form (a twin product splits purchase
/// history and blinds the predictor — exact hits are blocked, fuzzy ones ask), the tag-cloud and
/// filter row, the always-available "Out" button (the grid is the home for marking ANY product
/// out; the dashboard only lists what's already low), and the guarded delete.
/// </summary>
public class ProductsPageTests : PageTestContext
{

    private int Seed(string name, Action<Product>? configure = null)
    {
        using var db = Db.CreateDbContext();
        var product = new Product { Name = name, Category = Category.Pantry };
        configure?.Invoke(product);
        db.Products.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    private IRenderedComponent<Products> RenderGrid()
    {
        var cut = Render<Products>();
        cut.WaitForState(() => cut.FindAll("table").Count > 0);
        return cut;
    }

    private void SubmitAdd(IRenderedComponent<Products> cut, string name)
    {
        cut.Find("input[aria-label='New product name']").Change(name);
        cut.Find("form[aria-label='Add product']").Submit();
    }

    private static List<string> ShownNames(IRenderedComponent<Products> cut) =>
        [.. cut.FindAll("tbody .item-name").Select(a => a.TextContent.Trim())];

    // ------------------------------------------------------------------- add + duplicate guard

    [Fact]
    public async Task Adding_a_product_stores_name_category_and_unit_and_clears_the_form()
    {
        var cut = RenderGrid();

        cut.Find("select[aria-label='New product category']").Change("Dairy");
        cut.Find("input[aria-label='Default unit (optional)']").Change("gal");
        SubmitAdd(cut, "  Whole Milk ");

        cut.WaitForAssertion(() => Assert.Equal(["Whole Milk"], ShownNames(cut)));
        Assert.Equal("", cut.Find("input[aria-label='New product name']").GetAttribute("value") ?? "");

        await using var raw = Db.CreateUnscopedContext();
        var product = await raw.Products.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("Whole Milk", product.Name);
        Assert.Equal(Category.Dairy, product.Category);
        Assert.Equal("gal", product.DefaultUnit);
    }

    [Fact]
    public void A_blank_name_is_refused()
    {
        var cut = RenderGrid();
        SubmitAdd(cut, "   ");
        cut.WaitForAssertion(() => Assert.Equal("Name is required.", cut.Find("p.error").TextContent.Trim()));
    }

    [Fact]
    public async Task An_exact_duplicate_is_blocked_outright_with_a_link_to_the_original()
    {
        var id = Seed("Whole Milk");
        var cut = RenderGrid();

        SubmitAdd(cut, "whole milk"); // case differences are the same product

        cut.WaitForAssertion(() =>
        {
            var prompt = cut.Find(".dup-check");
            Assert.Contains("You already have", prompt.TextContent);
            Assert.Equal($"/product/{id}", prompt.QuerySelector("a")!.GetAttribute("href"));
            // Blocked, not asked: no "Add anyway" exists for an exact hit — two same-named products
            // would make every later match ambiguous.
            Assert.DoesNotContain("anyway", prompt.TextContent);
        });

        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(1, await raw.Products.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_fuzzy_duplicate_asks_and_the_user_can_overrule_it()
    {
        Seed("93% Lean Ground Beef");
        var cut = RenderGrid();

        SubmitAdd(cut, "lean ground beef");

        // Fuzzy can false-positive, so the human staring at the form gets the final say.
        cut.WaitForAssertion(() =>
            Assert.Contains("you might already have this", cut.Find(".dup-check").TextContent));
        await using (var raw = Db.CreateUnscopedContext())
        {
            Assert.Equal(1, await raw.Products.IgnoreQueryFilters().CountAsync()); // nothing yet
        }

        cut.FindAll(".dup-check button").Single(b => b.TextContent.Contains("anyway")).Click();

        cut.WaitForAssertion(() => Assert.Contains("lean ground beef", ShownNames(cut)));
        await using (var raw = Db.CreateUnscopedContext())
        {
            Assert.Equal(2, await raw.Products.IgnoreQueryFilters().CountAsync());
        }
    }

    [Fact]
    public async Task Never_mind_walks_away_from_a_fuzzy_match_without_creating()
    {
        Seed("93% Lean Ground Beef");
        var cut = RenderGrid();
        SubmitAdd(cut, "lean ground beef");
        cut.WaitForState(() => cut.FindAll(".dup-check").Count == 1);

        cut.FindAll(".dup-check button").Single(b => b.TextContent.Trim() == "Never mind").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".dup-check")));
        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(1, await raw.Products.IgnoreQueryFilters().CountAsync());
    }

    // ------------------------------------------------------------------------ filters + tag cloud

    [Fact]
    public void The_tag_cloud_filters_the_grid_and_the_untagged_chip_is_its_complement()
    {
        Seed("Ketchup", p => p.Tags = [new ProductTag { Value = "condiment" }]);
        Seed("Mustard", p => p.Tags = [new ProductTag { Value = "condiment" }]);
        Seed("Mystery Jar");
        var cut = RenderGrid();
        Assert.Equal(3, ShownNames(cut).Count);

        cut.FindAll(".tag-cloud-chip").Single(c => c.TextContent.Contains("condiment")).Click();
        cut.WaitForAssertion(() => Assert.Equal(["Ketchup", "Mustard"], ShownNames(cut)));

        // The cleanup chip is the complement — "has this tag" and "has no tags" can't both hold,
        // so choosing one stands the other down instead of intersecting to nothing.
        cut.Find(".untagged-chip").Click();
        cut.WaitForAssertion(() => Assert.Equal(["Mystery Jar"], ShownNames(cut)));

        cut.Find(".untagged-chip").Click();
        cut.WaitForAssertion(() => Assert.Equal(3, ShownNames(cut).Count)); // toggled off = all
    }

    [Fact]
    public void Search_and_status_filters_narrow_the_grid()
    {
        Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m }, // overdue
        ]);
        Seed("Yogurt", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-16), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-1), Quantity = 1m }, // stocked
        ]);
        var cut = RenderGrid();

        cut.Find("input[type=search]").Input("milk");
        cut.WaitForAssertion(() => Assert.Equal(["Whole Milk"], ShownNames(cut)));
        cut.Find("input[type=search]").Input("");

        cut.Find("select[aria-label='Filter by status']").Change("Overdue");
        cut.WaitForAssertion(() => Assert.Equal(["Whole Milk"], ShownNames(cut)));

        cut.Find("select[aria-label='Filter by status']").Change("Stocked");
        cut.WaitForAssertion(() => Assert.Equal(["Yogurt"], ShownNames(cut)));
    }

    [Fact]
    public void Deep_links_arrive_with_the_filters_already_applied()
    {
        Seed("Ketchup", p => p.Tags = [new ProductTag { Value = "condiment" }]);
        Seed("Frozen Peas", p => p.Category = Category.Frozen);
        Seed("Apples", p => p.Category = Category.Produce);
        Seed("Mystery Jar");

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/products?categories=Frozen,Produce,NotACategory");
        var cut = RenderGrid();

        // Multi-aisle deep link (the aisle chart's pooled remainder): unknown names are skipped,
        // the active set is NAMED on screen (invisible filtering reads as missing products), and
        // one tap clears it.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["Apples", "Frozen Peas"], ShownNames(cut));
            Assert.Contains("Showing aisles: Frozen, Produce", cut.Find(".active-aisle-filter").TextContent);
        });

        cut.Find(".active-aisle-filter .tag-clear").Click();
        cut.WaitForAssertion(() => Assert.Equal(4, ShownNames(cut).Count));
    }

    // ------------------------------------------------------------------------------- row actions

    [Fact]
    public async Task The_out_button_files_the_outage_and_the_row_reads_due_today()
    {
        var id = Seed("Dog Food", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-16), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-1), Quantity = 1m }, // freshly stocked
        ]);
        var cut = RenderGrid();
        Assert.Contains("Stocked", cut.Find("tbody .chip").TextContent);

        cut.Find("button.mark-out").Click();

        // A human's outage outranks the fresh purchase: the row pins Overdue with the outage date
        // as the effective due date ("due today", not "overdue by the old rhythm").
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Overdue", cut.Find("tbody .chip").TextContent);
            Assert.Contains("(today)", cut.Find("td.nextbuy").TextContent);
        });

        await using var raw = Db.CreateUnscopedContext();
        var signal = Assert.Single(await raw.InventorySignals.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SignalKind.OutNow, signal.Kind);
        Assert.Equal(id, signal.ProductId);
    }

    [Fact]
    public async Task The_tracking_checkbox_writes_through()
    {
        var id = Seed("Seasonal Thing");
        var cut = RenderGrid();

        cut.Find("tbody input[type=checkbox]").Change(false);

        await cut.WaitForAssertionAsync(async () =>
        {
            await using var raw = Db.CreateUnscopedContext();
            Assert.False((await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == id)).IsTracked);
        });
    }

    [Fact]
    public async Task Delete_asks_first_and_no_means_no()
    {
        Seed("Precious History", p => p.Purchases =
            [new PurchaseEvent { PurchasedAt = Today.AddDays(-3), Quantity = 1m }]);

        // One click destroys purchase history — the confirm is the guard.
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(false);
        var cut = RenderGrid();

        cut.Find("button[aria-label='Delete Precious History']").Click();

        await using (var raw = Db.CreateUnscopedContext())
        {
            Assert.Equal(1, await raw.Products.IgnoreQueryFilters().CountAsync());
        }

        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        cut.Find("button[aria-label='Delete Precious History']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("tbody .item-name")));
        await using (var raw = Db.CreateUnscopedContext())
        {
            Assert.Equal(0, await raw.Products.IgnoreQueryFilters().CountAsync());
            Assert.Empty(await raw.PurchaseEvents.IgnoreQueryFilters().ToListAsync()); // cascaded
        }
    }

    [Fact]
    public void A_suppressed_row_speaks_in_the_count_note_never_a_raw_date()
    {
        // The third surface to carry this rule (grocery list, products grid, chat) — a suppressed
        // row printing its rhythm date raw reads "Stocked · 15 days overdue" in one cell.
        Seed("Canned Beans", p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = new DateTimeOffset(Today.AddDays(-3).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            p.Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
            ];
        });
        var cut = RenderGrid();

        var cell = cut.Find("td.nextbuy");
        Assert.Contains("You have 5", cell.TextContent);
        Assert.DoesNotContain("overdue", cell.TextContent);
    }
}
