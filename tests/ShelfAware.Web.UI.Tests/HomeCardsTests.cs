using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
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
    public async Task Still_in_stock_snoozes_the_card_off_running_low_without_writing_a_purchase()
    {
        // The honest alternative to Restocked: "the prediction was early — I never ran out". A status-only
        // signal that the engine snoozes (not a fresh-supply re-anchor), so the card stands down.
        var id = Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
        ]);
        var cut = RenderHome();
        cut.WaitForState(() => cut.FindAll(".cards li").Count == 1);

        // "Still in stock" is the split button's SECOND alternate action, beneath Restocked.
        cut.Find(".cards .split-caret").Click();
        cut.WaitForState(() => cut.FindAll(".split-menu").Count == 1);
        cut.FindAll(".split-menu button").Single(b => b.TextContent.Trim() == "Still in stock").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Marked Whole Milk still in stock", cut.Find(".inline-confirm").TextContent));
        Assert.Empty(cut.FindAll(".cards li")); // snoozed off Running Low

        // Status-only: a StillInStock signal, NOT a purchase — it must never feed the cadence.
        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(2, await raw.PurchaseEvents.IgnoreQueryFilters().CountAsync(x => x.ProductId == id));
        var signal = Assert.Single(await raw.InventorySignals.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SignalKind.StillInStock, signal.Kind);
    }

    [Fact]
    public async Task Bought_today_offers_an_inline_undo_that_reverses_the_purchase()
    {
        var id = Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
        ]);
        var cut = RenderHome();
        cut.WaitForState(() => cut.FindAll(".cards li").Count == 1);

        cut.FindAll(".cards button").Single(b => b.TextContent.Trim() == "Bought today").Click();
        cut.WaitForAssertion(() =>
            Assert.Contains("Bought 1 × Whole Milk", cut.Find(".inline-confirm").TextContent));

        // Undo right there — the same one-service undo the /history page uses.
        cut.Find(".inline-confirm").QuerySelectorAll("button").Single(b => b.TextContent.Contains("Undo")).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".inline-confirm"))); // notice cleared
        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(2, await raw.PurchaseEvents.IgnoreQueryFilters().CountAsync(x => x.ProductId == id)); // manual buy reversed
    }

    [Fact]
    public async Task Restocked_offers_an_inline_undo_that_reverses_the_signal()
    {
        var id = Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
        ]);
        var cut = RenderHome();
        cut.WaitForState(() => cut.FindAll(".cards li").Count == 1);

        cut.Find(".cards .split-caret").Click();
        cut.WaitForState(() => cut.FindAll(".split-menu").Count == 1);
        cut.FindAll(".split-menu button").Single(b => b.TextContent.Trim() == "Restocked").Click();
        cut.WaitForAssertion(() =>
            Assert.Contains("Restocked Whole Milk", cut.Find(".inline-confirm").TextContent));

        cut.Find(".inline-confirm").QuerySelectorAll("button").Single(b => b.TextContent.Contains("Undo")).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".inline-confirm")));
        await using var raw = Db.CreateUnscopedContext();
        Assert.Empty(await raw.InventorySignals.IgnoreQueryFilters().ToListAsync()); // the restock signal reversed
    }

    [Fact]
    public async Task Dismissing_the_inline_notice_is_not_an_undo()
    {
        var id = Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
        ]);
        var cut = RenderHome();
        cut.WaitForState(() => cut.FindAll(".cards li").Count == 1);
        cut.FindAll(".cards button").Single(b => b.TextContent.Trim() == "Bought today").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".inline-confirm")));

        cut.Find(".inline-confirm").QuerySelectorAll("button").Single(b => b.TextContent.Contains("Dismiss")).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".inline-confirm")));
        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(3, await raw.PurchaseEvents.IgnoreQueryFilters().CountAsync(x => x.ProductId == id)); // buy stays
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
    public void Copy_name_puts_the_bare_item_name_on_the_clipboard_and_says_so()
    {
        // Brand and size render on the card as hints — the copy is the BARE item name, the form
        // that pastes usefully into a store search.
        Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-40), Quantity = 1m, Brand = "Great Value", Size = "1 gal" },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-25), Quantity = 1m, Brand = "Great Value", Size = "1 gal" },
        ]);
        var cut = RenderHome();
        cut.WaitForState(() => cut.FindAll(".cards li").Count == 1);

        var button = cut.Find(".copy-name");
        Assert.Equal("Copy Whole Milk to the clipboard", button.GetAttribute("aria-label"));
        // The announcer must exist BEFORE any copy: a live region reliably announces text
        // CHANGING inside an existing region, not a region inserted already holding its text.
        Assert.Equal("", cut.Find(".copy-announcer").TextContent.Trim());
        Assert.Equal("status", cut.Find(".copy-announcer").GetAttribute("role"));
        button.Click();

        var copy = JSInterop.Invocations.Single(i => i.Identifier == "navigator.clipboard.writeText");
        Assert.Equal("Whole Milk", copy.Arguments[0]);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Copied", cut.Find(".copy-note").TextContent.Trim());
            // The announcement carries the name — a screen reader hears WHICH card copied.
            Assert.Equal("Copied Whole Milk", cut.Find(".copy-announcer").TextContent.Trim());
        });
    }

    [Fact]
    public void The_copied_note_sits_on_the_card_that_was_copied_and_moves_with_the_next_copy()
    {
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
        var cut = RenderHome();
        cut.WaitForState(() => cut.FindAll(".cards li").Count == 2);

        cut.FindAll(".cards li").Single(li => li.TextContent.Contains("Due Soonish"))
            .QuerySelector(".copy-name")!.Click();
        cut.WaitForAssertion(() =>
        {
            var withNote = Assert.Single(cut.FindAll(".cards li"), li => li.QuerySelector(".copy-note") is not null);
            Assert.Contains("Due Soonish", withNote.TextContent);
        });

        // Copying another card MOVES the one note — "Copied" must never linger on a card it
        // no longer describes.
        cut.FindAll(".cards li").Single(li => li.TextContent.Contains("Rhythm Overdue"))
            .QuerySelector(".copy-name")!.Click();
        cut.WaitForAssertion(() =>
        {
            var withNote = Assert.Single(cut.FindAll(".cards li"), li => li.QuerySelector(".copy-note") is not null);
            Assert.Contains("Rhythm Overdue", withNote.TextContent);
        });
    }

    [Fact]
    public async Task A_reload_resets_copy_feedback_so_the_announcer_never_remounts_pre_filled()
    {
        // The cards can empty and later repopulate within one circuit (a voice change pings the
        // coordinator). An announcer remounting WITH old text is the inserted-with-its-text shape
        // it exists to avoid — and a stale claim besides.
        Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-40), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-25), Quantity = 1m },
        ]);
        var cut = RenderHome();
        cut.WaitForState(() => cut.FindAll(".cards li").Count == 1);
        cut.Find(".copy-name").Click();
        cut.WaitForAssertion(() =>
            Assert.Equal("Copied Whole Milk", cut.Find(".copy-announcer").TextContent.Trim()));

        await Coordinator.NotifyPantryChangedAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("", cut.Find(".copy-announcer").TextContent.Trim());
            Assert.Empty(cut.FindAll(".copy-note"));
        });
    }

    [Fact]
    public void A_refused_clipboard_says_so_on_the_card_instead_of_claiming_a_copy()
    {
        // Clipboard access can be denied (permissions, insecure context) — the card must report
        // the failure, not claim "Copied", and the click must not tear down the circuit.
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true)
            .SetException(new JSException("Write permission denied."));
        Seed("Whole Milk", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-40), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-25), Quantity = 1m },
        ]);
        var cut = RenderHome();
        cut.WaitForState(() => cut.FindAll(".cards li").Count == 1);

        cut.Find(".copy-name").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Couldn't copy", cut.Find(".copy-note").TextContent.Trim());
            Assert.Equal("Couldn't copy Whole Milk", cut.Find(".copy-announcer").TextContent.Trim());
        });
    }

    // ------------------------------------------------------------------ "Coming up this week"

    [Fact]
    public void A_stocked_item_due_within_the_week_shows_under_coming_up()
    {
        // ~14-day rhythm, last bought 10 days ago → due in 4 days. The Due-soon window is only 3 days,
        // so it reads Stocked and never reaches Running Low — exactly the slow-mover that used to hide.
        Seed("Chicken Breast", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-38), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-24), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-10), Quantity = 1m },
        ]);
        var cut = RenderHome();

        cut.WaitForAssertion(() =>
        {
            var panel = cut.Find(".coming-up");
            Assert.Contains("Chicken Breast", panel.TextContent);
            Assert.Contains("Due in 4 days", panel.TextContent);
        });
        Assert.Empty(cut.FindAll(".cards li"));  // not Running Low — it's a heads-up, not a nag
        // One item, one section: a coming-up item is pulled OUT of the Everything-else catch-all.
        Assert.DoesNotContain("Chicken Breast", cut.Find("details.everything-else").TextContent);
    }

    [Fact]
    public void A_stocked_item_due_beyond_the_week_stays_in_everything_else()
    {
        // ~30-day rhythm, last bought 10 days ago → due in 20 days: stocked, but too far off to be a
        // "this week" heads-up. It belongs in the collapsed Everything-else list, not Coming up.
        Seed("Paper Towels", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-70), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-40), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-10), Quantity = 1m },
        ]);
        var cut = RenderHome();

        cut.WaitForAssertion(() =>
            Assert.Contains("Paper Towels", cut.Find("details.everything-else").TextContent));
        Assert.Empty(cut.FindAll(".coming-up")); // no coming-up section at all
    }

    [Fact]
    public void Coming_up_and_running_low_are_separate_lists()
    {
        // Due in ~2 days → DueSoon → Running Low. Its due date is WITHIN the week, so only the
        // Stocked-status filter keeps it out of Coming up (it's already a nag, not a heads-up).
        Seed("Due Soon Item", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-26), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-12), Quantity = 1m },
        ]);
        Seed("Soon-ish Item", p => p.Purchases =        // ~14-day rhythm, last bought 10 days ago → due in 4, Stocked
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-38), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-24), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today.AddDays(-10), Quantity = 1m },
        ]);
        var cut = RenderHome();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Due Soon Item", cut.Find(".cards").TextContent);
            Assert.Contains("Soon-ish Item", cut.Find(".coming-up").TextContent);
        });
        Assert.DoesNotContain("Soon-ish Item", cut.Find(".cards").TextContent);
        Assert.DoesNotContain("Due Soon Item", cut.Find(".coming-up").TextContent);
    }

    [Fact]
    public void A_count_suppressed_item_is_not_a_coming_up_heads_up()
    {
        // Due in ~2 days by rhythm, but a fresh count of plenty holds the buy back (§13.5) → Stocked,
        // SuppressedByCount. A "you'll need this soon" nudge would contradict the count you just took,
        // so it is excluded — and (Assert.Empty on .cards) the suppression really did fire.
        Seed("Canned Beans", p =>
        {
            p.Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-26), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-12), Quantity = 1m },
            ];
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = DateTimeOffset.Now;
        });
        var cut = RenderHome();

        cut.WaitForState(() => cut.FindAll(".quick-update").Count > 0);
        Assert.Empty(cut.FindAll(".cards li"));  // the count suppressed the buy → not Running Low
        Assert.Empty(cut.FindAll(".coming-up")); // …and it must not resurface as a "coming up" nudge either
    }

    [Fact]
    public void A_fresh_counted_item_is_not_a_coming_up_heads_up_even_when_the_rhythm_alone_would_show_it()
    {
        // ~14-day rhythm, last bought 10 days ago → due in 4 → Stocked BY RHYTHM, so the count does NOT
        // suppress (suppression fires only when the rhythm would ask). But you counted 5 today — you're
        // managing this by count, so a proactive "Coming up" nudge would bother you about exactly that
        // (item 28). Excluding CountConfidence.Counted (not just SuppressedByCount) is what covers it.
        Seed("Canned Beans", p =>
        {
            p.Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-38), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-24), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-10), Quantity = 1m },
            ];
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = DateTimeOffset.Now;
        });
        var cut = RenderHome();

        cut.WaitForState(() => cut.FindAll(".quick-update").Count > 0);
        Assert.Empty(cut.FindAll(".cards li"));  // not Running Low (Stocked)…
        Assert.Empty(cut.FindAll(".coming-up")); // …and a fresh count keeps it out of Coming up too
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
