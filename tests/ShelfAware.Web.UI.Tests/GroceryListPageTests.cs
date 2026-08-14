using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The grocery list beyond "Used one": the manual Extras section, the row actions with genuinely
/// different meanings (Restocked = "I have some" vs Untrack = "don't want it for a while"), the
/// copy/download text the household actually shops from, and the aisle-then-urgency ordering that
/// makes the page read as one walk through the store.
/// </summary>
public class GroceryListPageTests : PageTestContext
{

    /// <summary>Overdue on a 15-day rhythm — the shape that lands a row in "Buy now".</summary>
    private int SeedOverdue(string name, Category category = Category.Dairy, string? brand = null, string? size = null)
    {
        using var db = Db.CreateDbContext();
        var product = new Product
        {
            Name = name,
            Category = category,
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m, Brand = brand, Size = size },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m, Brand = brand, Size = size },
            ],
        };
        db.Products.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    private IRenderedComponent<GroceryList> RenderList()
    {
        var cut = Render<GroceryList>();
        cut.WaitForState(() => cut.FindAll(".extras").Count > 0);
        return cut;
    }

    private void AddExtra(IRenderedComponent<GroceryList> cut, string name)
    {
        cut.Find("input[aria-label='Add an item to the grocery list']").Input(name);
        cut.FindAll(".tag-add button").Single(b => b.TextContent.Trim() == "Add").Click();
    }

    // ------------------------------------------------------------------------------------ extras

    [Fact]
    public async Task Extras_add_trimmed_dedupe_case_insensitively_and_render_sorted()
    {
        var cut = RenderList();

        AddExtra(cut, "  Zip Bags ");
        cut.WaitForState(() => cut.Find(".extras .tag-list").TextContent.Contains("Zip Bags"));
        AddExtra(cut, "Batteries");
        cut.WaitForState(() => cut.Find(".extras .tag-list").TextContent.Contains("Batteries"));

        AddExtra(cut, "zip bags"); // the same item, typed casually — must not double up

        cut.WaitForAssertion(() =>
        {
            var chips = cut.FindAll(".extras .tag-chip").Select(c => c.TextContent.TrimEnd('×').Trim()).ToList();
            Assert.Equal(["Batteries", "Zip Bags"], chips); // alphabetical, deduped, whitespace trimmed
            Assert.Equal("", cut.Find("input[aria-label='Add an item to the grocery list']").GetAttribute("value") ?? "");
        });

        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(2, await raw.GroceryExtras.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Removing_an_extra_already_gone_is_a_no_op_not_an_error()
    {
        var cut = RenderList();
        AddExtra(cut, "Batteries");
        cut.WaitForState(() => cut.FindAll(".extras .tag-x").Count == 1);

        // The other half of the household removes it first (or a double tap lands twice) — the
        // page's filtered lookup misses, and nothing-to-delete is a no-op, never a crash.
        using (var db = Db.CreateDbContext())
        {
            db.RemoveRange(db.GroceryExtras);
            db.SaveChanges();
        }

        cut.Find(".extras .tag-x").Click();

        cut.WaitForAssertion(() => Assert.Contains("Nothing extra yet.", cut.Find(".extras .tag-list").TextContent));
        Assert.Empty(cut.FindAll(".error"));
        await using var raw = Db.CreateUnscopedContext();
        Assert.Empty(await raw.GroceryExtras.IgnoreQueryFilters().ToListAsync());
    }

    // ------------------------------------------------------------------------------- row actions

    [Fact]
    public async Task Restocked_clears_the_nag_re_anchors_and_stales_the_copied_status()
    {
        var id = SeedOverdue("Whole Milk");
        var cut = RenderList();
        cut.WaitForState(() => cut.FindAll(".restock-btn").Count == 1);

        cut.FindAll(".list-actions button").Single(b => b.TextContent.Trim() == "Copy list").Click();
        cut.WaitForState(() => cut.FindAll("[role=status]").Any(s => s.TextContent.Contains("Copied")));

        cut.Find(".restock-btn").Click();

        cut.WaitForAssertion(() =>
        {
            // Status-only: the row settles into "Coming up" (re-anchored, not repurchased)…
            Assert.Contains("Coming up", cut.Markup);
            Assert.Empty(cut.FindAll(".restock-btn")); // …and leaves Buy now
            // …and the "Copied" claim disappears: the clipboard now holds a stale list.
            Assert.DoesNotContain("Copied to clipboard", cut.Markup);
        });

        await using var raw = Db.CreateUnscopedContext();
        var signal = Assert.Single(await raw.InventorySignals.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SignalKind.Restocked, signal.Kind);
        Assert.Equal(id, signal.ProductId);
        // The two-stream rule: a restock never becomes a purchase.
        Assert.Equal(2, await raw.PurchaseEvents.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Untrack_stops_suggesting_but_keeps_the_history()
    {
        var id = SeedOverdue("Whole Milk");
        var cut = RenderList();
        cut.WaitForState(() => cut.FindAll(".untrack-btn").Count == 1);

        cut.Find(".untrack-btn").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("No tracked products yet.", cut.Markup));

        await using var raw = Db.CreateUnscopedContext();
        var product = await raw.Products.IgnoreQueryFilters().Include(p => p.Purchases).SingleAsync(p => p.Id == id);
        Assert.False(product.IsTracked);
        Assert.Equal(2, product.Purchases.Count); // "for a while", not "forget it ever existed"
    }

    // ------------------------------------------------------------------------- copy and download

    [Fact]
    public void Copy_writes_the_shoppable_text_with_qualifiers_and_extras()
    {
        SeedOverdue("Whole Milk", brand: "Great Value", size: "gal");
        var cut = RenderList();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0);
        AddExtra(cut, "Batteries");
        cut.WaitForState(() => cut.Find(".extras .tag-list").TextContent.Contains("Batteries"));

        cut.FindAll(".list-actions button").Single(b => b.TextContent.Trim() == "Copy list").Click();

        // The copied text is what gets shopped from — the qualifiers (brand, size) and the manual
        // extras have to survive the trip to the clipboard, not just the screen.
        var copy = JSInterop.Invocations.Single(i => i.Identifier == "navigator.clipboard.writeText");
        var text = (string)copy.Arguments[0]!;
        Assert.Contains("- Whole Milk (Great Value, gal) ×1", text);
        Assert.Contains("Extras:", text);
        Assert.Contains("- Batteries", text);
    }

    [Fact]
    public void A_refused_clipboard_reports_couldnt_copy_instead_of_tearing_down_the_page()
    {
        // Pre-fix, an unhandled JSException here tore down the whole circuit — on the page whose
        // Copy button is a headline feature.
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true)
            .SetException(new JSException("Write permission denied."));
        SeedOverdue("Whole Milk");
        var cut = RenderList();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0);

        cut.FindAll(".list-actions button").Single(b => b.TextContent.Trim() == "Copy list").Click();

        cut.WaitForAssertion(() => Assert.Equal("Couldn't copy", cut.Find(".copy-note").TextContent.Trim()));
        Assert.NotEmpty(cut.FindAll("tbody tr")); // the page survived the refusal
    }

    [Fact]
    public void Download_names_the_file_for_today_and_the_shortcut_reaches_the_same_road()
    {
        SeedOverdue("Whole Milk");
        var cut = RenderList();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0);

        // The page wired the keyboard shortcut on first render…
        cut.WaitForAssertion(() =>
            Assert.Contains(JSInterop.Invocations, i => i.Identifier == "registerShortcut"));

        cut.FindAll(".list-actions button").Single(b => b.TextContent.Trim() == "Download").Click();

        cut.WaitForAssertion(() =>
        {
            var download = JSInterop.Invocations.Single(i => i.Identifier == "downloadText");
            Assert.Equal($"grocery-list-{Today:yyyy-MM-dd}.txt", (string)download.Arguments[0]!);
        });
    }

    // ------------------------------------------------------------------------------------ layout

    [Fact]
    public void Rows_walk_the_store_by_aisle_then_urgency_within_it()
    {
        // Two dairy rows at different urgency plus a pantry row more urgent than both: the aisle
        // wins first (dairy before pantry in store order), urgency only orders within the aisle —
        // a pure-urgency sort would put the beans on top and scatter the walk.
        SeedOverdue("Whole Milk", Category.Dairy);                       // due 15 days ago
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Yogurt",
                Category = Category.Dairy,
                Purchases =
                [
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-40), Quantity = 1m },
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-20), Quantity = 1m }, // due today
                ],
            });
            db.Products.Add(new Product
            {
                Name = "Canned Beans",
                Category = Category.Pantry,
                Purchases =
                [
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-75), Quantity = 1m },
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-60), Quantity = 1m }, // due 45 days ago
                ],
            });
            db.SaveChanges();
        }
        var cut = RenderList();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count == 3);

        var names = cut.FindAll("tbody tr .item-name").Select(a => a.TextContent.Trim()).ToList();
        Assert.Equal(["Whole Milk", "Yogurt", "Canned Beans"], names);
    }

    [Fact]
    public void Still_learning_names_the_products_that_need_more_buys()
    {
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "New Thing",
                Category = Category.Pantry,
                Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-3), Quantity = 1m }],
            });
            db.SaveChanges();
        }
        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            var details = cut.Find("details.everything-else");
            Assert.Contains("Still learning (1)", details.TextContent);
            Assert.Contains("New Thing", details.TextContent);
        });
    }
}
