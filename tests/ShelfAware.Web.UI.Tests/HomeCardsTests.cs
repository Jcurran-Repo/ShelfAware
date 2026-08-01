using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The dashboard's cards: what qualifies as Running Low, the §8 ordering (a human-pinned outage
/// outranks everything the engine merely inferred), the quick-buy actions' opposite semantics
/// (Bought today feeds the rhythm; Restocked never does), the honest reasons on a card (the label,
/// the signal note), and the coordinator ping that keeps the page live under the roaming voice
/// agent.
/// </summary>
public class HomeCardsTests : PageTestContext
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

    private IRenderedComponent<Home> RenderHome()
    {
        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".quick-update").Count > 0);
        return cut;
    }

    [Fact]
    public void Cards_order_pinned_outages_first_then_severity_then_date()
    {
        // Overdue by rhythm (-10), due soon (+2), and a human-said outage that is merely due today
        // by date — §8: the pin outranks both, because a person SAID it, and the note says so.
        Seed("Rhythm Overdue", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-40), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-25), Quantity = 1m },
        ]);
        Seed("Due Soonish", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-28), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-13), Quantity = 1m },
        ]);
        Seed("Said Out Loud", p =>
        {
            p.Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-35), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-20), Quantity = 1m },
            ];
            p.Signals = [new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = DateTimeOffset.Now }];
        });
        var cut = RenderHome();

        cut.WaitForAssertion(() =>
        {
            var names = cut.FindAll(".cards .card-name").Select(a => a.TextContent.Trim()).ToList();
            Assert.Equal(["Said Out Loud", "Rhythm Overdue", "Due Soonish"], names);
        });
        // The pinned card carries the human's own statement, marked as a pin.
        var pinned = cut.FindAll(".cards li").First();
        Assert.Contains("📌", pinned.TextContent);
    }

    [Fact]
    public async Task The_summary_chips_count_by_status_and_the_quiet_state_says_so()
    {
        var cut = RenderHome();
        cut.WaitForAssertion(() => Assert.Contains("You're all stocked up.", cut.Find(".summary").TextContent));

        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Overdue Thing",
                Category = Category.Pantry,
                Purchases =
                [
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-40), Quantity = 1m },
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-25), Quantity = 1m },
                ],
            });
            db.Products.Add(new Product
            {
                Name = "Soonish Thing",
                Category = Category.Pantry,
                Purchases =
                [
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-28), Quantity = 1m },
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-13), Quantity = 1m },
                ],
            });
            db.SaveChanges();
        }
        // Reload through the coordinator ping — the road a voice data change takes to a page it
        // can't reach directly. The chips must then count what the cards show.
        await Coordinator.NotifyPantryChangedAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("1 overdue", cut.Find(".chip-overdue").TextContent.Trim());
            Assert.Equal("1 due soon", cut.Find(".chip-duesoon").TextContent.Trim());
        });
    }

    [Fact]
    public async Task Bought_today_records_a_manual_purchase_and_the_card_stands_down()
    {
        var id = Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
        ]);
        var cut = RenderHome();
        cut.WaitForState(() => cut.FindAll(".cards li").Count == 1);

        cut.FindAll(".cards button").Single(b => b.TextContent.Trim() == "Bought today").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".cards li"))); // fresh stock, no nag

        await using var raw = Db.CreateUnscopedContext();
        var purchases = await raw.PurchaseEvents.IgnoreQueryFilters()
            .Where(x => x.ProductId == id).OrderBy(x => x.PurchasedAt).ToListAsync();
        Assert.Equal(3, purchases.Count);
        var added = purchases[^1];
        // A real purchase: dated today, quantity 1, marked Manual — this one FEEDS the rhythm.
        Assert.Equal(Today, added.PurchasedAt);
        Assert.Equal(1m, added.Quantity);
        Assert.Equal(PurchaseSource.Manual, added.Source);
    }

    [Fact]
    public async Task Restocked_re_anchors_without_writing_a_purchase()
    {
        var id = Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
        ]);
        var cut = RenderHome();
        cut.WaitForState(() => cut.FindAll(".cards li").Count == 1);

        // Restocked is the split button's alternate action — behind the caret, deliberately less
        // prominent than the primary "Bought today".
        cut.Find(".cards .split-caret").Click();
        cut.WaitForState(() => cut.FindAll(".split-menu").Count == 1);
        cut.FindAll(".split-menu button").Single(b => b.TextContent.Trim() == "Restocked").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".cards li")));

        // The §6 two-stream rule, seen from the dashboard: a restock is status-only — it clears
        // the nag but must never count as a buy, or casual taps would corrupt the cadence.
        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(2, await raw.PurchaseEvents.IgnoreQueryFilters().CountAsync(x => x.ProductId == id));
        var signal = Assert.Single(await raw.InventorySignals.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SignalKind.Restocked, signal.Kind);
    }

    [Fact]
    public async Task An_expired_card_names_the_label_as_the_reason_it_is_red()
    {
        // The rhythm alone would say Stocked (bought 3 days ago) — the LABEL is why the card is
        // red, and the user can see the jug in the fridge, so the card must say which fact fired.
        await AppSettings.SetAsync(SettingKeys.TrackExpirationDates, "true");
        Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-10), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-3), Quantity = 1m, ExpirationDate = Today.AddDays(-1) },
        ]);
        var cut = RenderHome();

        cut.WaitForAssertion(() =>
        {
            var card = Assert.Single(cut.FindAll(".cards li"));
            Assert.Contains($"🏷️ Expired {Today.AddDays(-1):MMM d}", card.TextContent);
            Assert.Contains("Overdue", card.QuerySelector(".chip")!.TextContent);
        });
    }

    [Fact]
    public void The_habit_panel_surfaces_items_burned_faster_than_rebought()
    {
        // Bought every 20 days but empty after 10 (the OutNow closes the burn cycle): the item is
        // fine TODAY (just bought), so no card — the habit insight is the only place this shows.
        Seed("Coffee Beans", p =>
        {
            p.Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-40), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-20), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-1), Quantity = 1m },
            ];
            p.Signals =
            [
                new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = new DateTimeOffset(Today.AddDays(-30).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) },
                new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = new DateTimeOffset(Today.AddDays(-10).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) },
            ];
        });
        var cut = RenderHome();

        cut.WaitForAssertion(() =>
        {
            var panel = cut.Find(".runs-out-early");
            Assert.Contains("Coffee Beans", panel.TextContent);
            Assert.Contains("lasts ~10 days but you rebuy ~every 20", panel.TextContent);
            Assert.Contains("out ~10 days each cycle", panel.TextContent);
        });
    }

    [Fact]
    public void Learning_hints_count_purchases_only_because_restocks_never_taught_a_rhythm()
    {
        Seed("Brand New", p => p.Signals =
            [new InventorySignal { Kind = SignalKind.Restocked, SignaledAt = DateTimeOffset.Now }]);
        Seed("Half Learned", p =>
        {
            p.Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1m }];
            p.Signals = [new InventorySignal { Kind = SignalKind.Restocked, SignaledAt = DateTimeOffset.Now }];
        });
        var cut = RenderHome();

        // Counting restocks here once promised predictions that never came — the hint counts the
        // only thing the rhythm learns from.
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("details.everything-else tbody tr");
            Assert.Contains("2 more purchases to start predicting",
                rows.Single(r => r.TextContent.Contains("Brand New")).TextContent);
            Assert.Contains("1 more purchase to start predicting",
                rows.Single(r => r.TextContent.Contains("Half Learned")).TextContent);
        });
    }
}
