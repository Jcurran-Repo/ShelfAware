using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The expiration panel (v3.6): opt-in, human-entered, and derived — the label hard-caps the
/// rhythm's projection before it passes and pins the item out after, unless a later Restocked
/// overrides it. The panel is also the first surface to get the split failure advice this arc
/// added to <c>ApplyExpirationAsync</c> (audit bug #2 flagged the missing catch: a reload failure
/// there used to tear down the circuit).
/// </summary>
public class ProductDetailExpirationTests : PageTestContext
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private Task TrackExpirations() => AppSettings.SetAsync(SettingKeys.TrackExpirationDates, "true");

    private int Seed(Action<Product>? configure = null)
    {
        using var db = Db.CreateDbContext();
        var product = new Product { Name = "Whole Milk", Category = Category.Dairy };
        configure?.Invoke(product);
        db.Products.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    private IRenderedComponent<ProductDetail> RenderDetail(int id)
    {
        var cut = Render<ProductDetail>(ps => ps.Add(p => p.Id, id));
        cut.WaitForState(() => cut.FindAll("h1").Count > 0);
        return cut;
    }

    private static AngleSharp.Dom.IElement? Section(IRenderedComponent<ProductDetail> cut) =>
        cut.FindAll("section.panel").FirstOrDefault(s => s.QuerySelector("h2")?.TextContent.Trim() == "Expiration");

    private static string SectionText(IRenderedComponent<ProductDetail> cut) =>
        System.Text.RegularExpressions.Regex.Replace(Section(cut)!.TextContent, @"\s+", " ").Trim();

    [Fact]
    public void The_panel_does_not_exist_while_the_toggle_is_off()
    {
        // Off is DORMANT: dates kept, nothing renders anywhere. The panel absent — not disabled,
        // not empty — is the off state's whole contract on this page.
        var id = Seed(p => p.Purchases =
            [new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m, ExpirationDate = Today.AddDays(3) }]);
        var cut = RenderDetail(id);

        Assert.Null(Section(cut));
        Assert.DoesNotContain("capped by the expiration date", cut.Markup);
    }

    [Fact]
    public async Task With_no_purchases_the_panel_explains_there_is_nothing_to_carry_a_date()
    {
        await TrackExpirations();
        var cut = RenderDetail(Seed());

        Assert.Contains("No purchases yet — the date rides on a purchase, so record one first.", SectionText(cut));
        Assert.Empty(cut.FindAll("input[type=date]")); // no input — there is nothing it could write to
    }

    [Fact]
    public async Task A_label_inside_the_rhythms_projection_caps_the_next_buy_date()
    {
        // Rhythm projects +10 (bought -20/-5 on a 15-day cadence); the label says +3. The cadence
        // estimates how long stock usually lasts, the label bounds how long it CAN — min, never max.
        await TrackExpirations();
        var id = Seed(p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-20), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m, ExpirationDate = Today.AddDays(3) },
        ]);
        var cut = RenderDetail(id);

        Assert.Contains($"Expires {Today.AddDays(3):MMM d, yyyy} (in 3 days)", SectionText(cut));
        var nextBuy = cut.Find("dl.rhythm dd").TextContent;
        Assert.Contains($"{Today.AddDays(3):MMM d, yyyy}", nextBuy);
        Assert.Contains("capped by the expiration date", nextBuy);
    }

    [Fact]
    public async Task A_passed_label_pins_the_item_out_and_says_how_to_override()
    {
        await TrackExpirations();
        var id = Seed(p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-20), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m, ExpirationDate = Today.AddDays(-2) },
        ]);
        var cut = RenderDetail(id);

        // The pin is the feature: the label passed, so the item counts as out — and the panel must
        // say the way back (Restocked beats the sticker) or the household is stuck wondering.
        Assert.Contains($"Expired {Today.AddDays(-2):MMM d, yyyy} — counted as out.", SectionText(cut));
        Assert.Contains("mark it Restocked", SectionText(cut)); // the way back must be on screen
        Assert.Equal("Overdue", cut.Find(".page-head .chip").TextContent.Trim());
    }

    [Fact]
    public async Task Restocked_after_the_label_overrides_it_and_the_panel_says_so()
    {
        // "I froze it" beats the sticker — but only a restock dated AFTER the label day; the panel
        // must SAY overridden, or the human wonders why a date they set stopped counting.
        await TrackExpirations();
        var id = Seed(p =>
        {
            p.Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-20), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-10), Quantity = 1m, ExpirationDate = Today.AddDays(-5) },
            ];
            p.Signals = [new InventorySignal { Kind = SignalKind.Restocked, SignaledAt = DateTimeOffset.Now }];
        });
        var cut = RenderDetail(id);

        var text = SectionText(cut);
        Assert.Contains("overridden", text);
        Assert.Contains("your word beats the label", text);
        Assert.NotEqual("Overdue", cut.Find(".page-head .chip").TextContent.Trim());
    }

    [Fact]
    public async Task Saving_a_date_stamps_every_latest_day_purchase_and_only_those()
    {
        // The engine reads the latest day's LONGEST date, so a stale sibling on the same day would
        // silently outvote the user — the one write path stamps them all (and leaves history alone).
        await TrackExpirations();
        var id = Seed(p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-20), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m },
        ]);
        var cut = RenderDetail(id);

        cut.Find("input[type=date]").Change($"{Today.AddDays(14):yyyy-MM-dd}");
        Section(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Contains($"Expires {Today.AddDays(14):MMM d, yyyy}", SectionText(cut)));

        await using var raw = Db.CreateUnscopedContext();
        var purchases = await raw.PurchaseEvents.IgnoreQueryFilters().Where(x => x.ProductId == id).ToListAsync();
        Assert.Equal(2, purchases.Count(x => x.ExpirationDate == Today.AddDays(14)));
        Assert.Null(purchases.Single(x => x.PurchasedAt == Today.AddDays(-20)).ExpirationDate);
    }

    [Fact]
    public async Task Clear_removes_the_date_and_the_panel_returns_to_its_invitation()
    {
        await TrackExpirations();
        var id = Seed(p => p.Purchases =
            [new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m, ExpirationDate = Today.AddDays(9) }]);
        var cut = RenderDetail(id);
        Assert.Contains("Expires", SectionText(cut));

        Section(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Clear").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("No expiration date on the latest purchase.", SectionText(cut)));

        await using var raw = Db.CreateUnscopedContext();
        Assert.Null((await raw.PurchaseEvents.IgnoreQueryFilters().SingleAsync(x => x.ProductId == id)).ExpirationDate);
    }

    [Fact]
    public async Task Save_is_disabled_until_the_date_actually_changes()
    {
        // The input pre-fills with the governing date, so an untouched Save would be a no-op write —
        // disabled until the value differs, enabled the moment it does.
        await TrackExpirations();
        var id = Seed(p => p.Purchases =
            [new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m, ExpirationDate = Today.AddDays(9) }]);
        var cut = RenderDetail(id);

        var save = Section(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Save");
        Assert.True(save.HasAttribute("disabled"));

        cut.Find("input[type=date]").Change($"{Today.AddDays(20):yyyy-MM-dd}");
        cut.WaitForAssertion(() =>
        {
            var after = Section(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Save");
            Assert.False(after.HasAttribute("disabled"));
        });
    }

    [Fact]
    public async Task A_failed_write_advises_retry_and_no_date_landed()
    {
        await TrackExpirations();
        var id = Seed(p => p.Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m }]);
        var cut = RenderDetail(id);
        cut.Find("input[type=date]").Change($"{Today.AddDays(14):yyyy-MM-dd}");

        Factory.FailAfter = 0; // the store's write context dies
        Section(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "That didn't save. Reload the page and try again.",
            cut.Find("p.error").TextContent.Trim()));

        Factory.FailAfter = null;
        await using var raw = Db.CreateUnscopedContext();
        Assert.Null((await raw.PurchaseEvents.IgnoreQueryFilters().SingleAsync(x => x.ProductId == id)).ExpirationDate);
    }

    [Fact]
    public async Task A_failed_reload_after_a_landed_date_says_saved_not_retry()
    {
        // Audit bug #2's flagged sibling: before this arc, ApplyExpirationAsync had NO catch — this
        // exact failure tore down the circuit with the date already written.
        await TrackExpirations();
        var id = Seed(p => p.Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m }]);
        var cut = RenderDetail(id);
        cut.Find("input[type=date]").Change($"{Today.AddDays(14):yyyy-MM-dd}");

        Factory.FailAfter = 1; // the write lands; the reload's context dies
        Section(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Saved — but the page couldn't refresh. Reload to see it.",
            cut.Find("p.error").TextContent.Trim()));

        Factory.FailAfter = null;
        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(Today.AddDays(14),
            (await raw.PurchaseEvents.IgnoreQueryFilters().SingleAsync(x => x.ProductId == id)).ExpirationDate);
    }

    [Fact]
    public async Task A_save_racing_a_receipt_removal_gets_the_no_purchases_refusal()
    {
        // The date rides on purchases; a receipt removal in another tab can take the last one
        // between this page's load and the Save. The store refuses and the page says why.
        await TrackExpirations();
        var id = Seed(p => p.Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m }]);
        var cut = RenderDetail(id);
        cut.Find("input[type=date]").Change($"{Today.AddDays(14):yyyy-MM-dd}");

        using (var db = Db.CreateDbContext())
        {
            db.RemoveRange(db.PurchaseEvents.Where(x => x.ProductId == id));
            db.SaveChanges();
        }

        Section(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Couldn't save — this product has no purchases to carry a date.",
            cut.Find("p.error").TextContent.Trim()));
    }
}
