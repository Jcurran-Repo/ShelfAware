using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The product page's count panel — §13.5's confidence bands driven through the rendered markup.
/// One stored truth (the number and the date a human vouched for it); CountConfidence decides
/// whether the panel may ASSERT it ("4 on hand") or must ATTRIBUTE it ("you counted 12 on
/// Apr 22"), with a distrusted zero getting its own sentence. Writes: an empty box is not a zero,
/// a negative absolute is refused not clamped, and the failure advice splits on WHICH context
/// died — "Used one" is relative, so "try again" after a landed write takes a second package.
/// </summary>
public class ProductDetailCountPanelTests : PageTestContext
{

    private static DateTimeOffset Clock(int daysAgo) =>
        new(Today.AddDays(-daysAgo).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private int Seed(Action<Product> configure)
    {
        using var db = Db.CreateDbContext();
        var product = new Product { Name = "Canned Beans", Category = Category.Pantry };
        configure(product);
        db.Products.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    /// <summary>Whole-number purchases <paramref name="daysAgo"/> days back — a rhythm whose "one
    /// package" is exactly 1, so decrement arithmetic in the asserts stays readable.</summary>
    private static List<PurchaseEvent> Buys(params int[] daysAgo) =>
        [.. daysAgo.Select(d => new PurchaseEvent { PurchasedAt = Today.AddDays(-d), Quantity = 1m })];

    private IRenderedComponent<ProductDetail> RenderDetail(int id)
    {
        var cut = Render<ProductDetail>(ps => ps.Add(p => p.Id, id));
        cut.WaitForState(() => cut.FindAll("h1").Count > 0);
        return cut;
    }

    private static string CountPanel(IRenderedComponent<ProductDetail> cut) =>
        Collapsed(cut.FindAll("section.panel")
            .Single(s => s.QuerySelector("h2")?.TextContent.Trim() == "How many you have"));

    private static void Click(IRenderedComponent<ProductDetail> cut, string buttonText) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == buttonText).Click();

    private async Task<Product> LoadAsync(int id)
    {
        await using var raw = Db.CreateUnscopedContext();
        return await raw.Products.IgnoreQueryFilters().Include(p => p.Signals).SingleAsync(p => p.Id == id);
    }

    // ------------------------------------------------------- what a zero on the shelf is told it means

    [Fact]
    public void A_zero_a_receipt_or_a_cook_arrived_at_asks_the_household_to_confirm_it()
    {
        // §13.4's derived zero: cooking and receipt removals move the number without anyone claiming an
        // outage, so this is the hypothesis worth raising. Nothing pinned the item, and no OutNow was
        // ever filed — which is what tells the two kinds of zero apart.
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 0m;
            p.QuantityCountedAt = Clock(1);
            p.Purchases = Buys(30, 15);
        });

        var panel = CountPanel(RenderDetail(id));

        Assert.Contains("nothing here has said so out loud", panel);
        Assert.DoesNotContain("that's on record as running out", panel);
    }

    [Fact]
    public void A_zero_the_human_asserted_says_it_is_on_record()
    {
        // The complement, and the branch that must not swallow the one above: an active OutNow pins, and
        // asking someone to "say so" about an outage already on record implies the app ignored them.
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 0m;
            p.QuantityCountedAt = Clock(1);
            p.Purchases = Buys(30, 15);
            p.Signals = [new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = Clock(1) }];
        });

        var panel = CountPanel(RenderDetail(id));

        Assert.Contains("that's on record as running out", panel);
        Assert.DoesNotContain("nothing here has said so out loud", panel);
    }

    [Fact]
    public void A_zero_whose_outage_is_no_longer_in_force_says_so_rather_than_denying_it()
    {
        // ⚠️ The state the app's own recovery path produces — assert the outage, then tap Restocked,
        // which is how an outage is MEANT to be cleared. Restocked is status-only, so the number stays
        // at zero while the pin goes. Without its own branch this fell into the derived-zero copy and
        // told someone who had said they were out that nothing here said so.
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 0m;
            p.QuantityCountedAt = Clock(5);
            p.Purchases = Buys(30, 15);
            p.Signals =
            [
                new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = Clock(5) },
                new InventorySignal { Kind = SignalKind.Restocked, SignaledAt = Clock(1) },
            ];
        });

        var panel = CountPanel(RenderDetail(id));

        Assert.Contains("you've said so before", panel);
        Assert.Contains("not in force now", panel);
        Assert.DoesNotContain("nothing here has said so out loud", panel);
        // ⚠️ The copy must not name HOW the outage was cleared, nor claim list membership. The first
        // version did both and both were false — see the branch's comment.
        Assert.DoesNotContain("restocked", panel);
        Assert.DoesNotContain("isn't on the list", panel);
    }

    [Fact]
    public void A_superseded_outage_with_stock_recorded_today_stops_promising_an_act_that_wont_work()
    {
        // ⚠️ The advice trap: with the stock-back dated TODAY, a fresh 0 ties with it and loses —
        // permanently (§6.6's same-day rule; the signal's date never changes) — so "set it to 0
        // again" was a silent no-op that re-rendered this same branch, advice included, for the
        // rest of the day. The classic road in is a mis-tapped Restocked and a same-day attempt to
        // undo it. The page words what the engine states (OutNowTodayWouldBeInert); it derives
        // nothing itself.
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 0m;
            p.QuantityCountedAt = Clock(5);
            p.Purchases = Buys(30, 15);
            p.Signals =
            [
                new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = Clock(5) },
                new InventorySignal { Kind = SignalKind.Restocked, SignaledAt = Clock(0) },
            ];
        });

        var panel = CountPanel(RenderDetail(id));

        Assert.Contains("you've said so before", panel);
        Assert.Contains("wins a same-day tie", panel);
        Assert.Contains("set it to 0 then", panel);
        Assert.DoesNotContain("set it to 0 again", panel);
    }

    [Fact]
    public void A_derived_zero_with_stock_recorded_today_gets_the_same_honest_timing()
    {
        // The derived-zero advice makes the same promise ("set it to 0 and it counts as running
        // out"), so a same-day stock-back needs the same split — a promise the tie rule breaks
        // today must not be made today.
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 0m;
            p.QuantityCountedAt = Clock(1);
            p.Purchases = Buys(30, 0); // bought TODAY, cooked to zero the same day
        });

        var panel = CountPanel(RenderDetail(id));

        Assert.Contains("nothing here has said so out loud", panel);
        Assert.Contains("wins a same-day tie", panel);
        Assert.DoesNotContain("set it to 0 and it counts as running out", panel);
    }

    [Fact]
    public void A_later_running_low_that_displaces_the_outage_gets_the_same_mechanism_free_sentence()
    {
        // The not-in-force branch has a SECOND road in: no stock event anywhere, just a later
        // RunningLow displacing the OutNow as the active signal. The copy survives because it
        // names no mechanism — which is exactly why it must never regain one.
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 0m;
            p.QuantityCountedAt = Clock(5);
            p.Purchases = Buys(30, 15);
            p.Signals =
            [
                new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = Clock(5) },
                new InventorySignal { Kind = SignalKind.RunningLow, SignaledAt = Clock(2) },
            ];
        });

        var panel = CountPanel(RenderDetail(id));

        Assert.Contains("you've said so before", panel);
        Assert.Contains("not in force now", panel);
        Assert.DoesNotContain("nothing here has said so out loud", panel);
    }

    [Fact]
    public void A_zero_whose_outage_a_PURCHASE_cleared_gets_the_same_sentence()
    {
        // ⚠️ The mutation-killer, and the case the first version of this branch got wrong. There is no
        // Restocked here at all: the outage was superseded by BUYING some (the predictor's own primary
        // flow — "pinned Overdue → Bought today"), and the count came back to zero by cooking. The
        // branch keys on "an OutNow exists", so it must fire here too — which is exactly why the copy
        // cannot name a restock. Without this test, swapping the predicate to Restocked left the whole
        // UI suite green.
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 0m;
            p.QuantityCountedAt = Clock(10);
            p.Purchases = Buys(30, 4);   // the later buy is what cleared the outage
            p.Signals = [new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = Clock(10) }];
        });

        var panel = CountPanel(RenderDetail(id));

        Assert.Contains("you've said so before", panel);
        Assert.DoesNotContain("restocked", panel);
        Assert.DoesNotContain("nothing here has said so out loud", panel);
    }

    // ------------------------------------------------------------------- the confidence bands

    [Fact]
    public void A_believed_count_asserts_the_number_and_names_the_look_it_rests_on()
    {
        var id = Seed(p =>
        {
            p.DefaultUnit = "cans";
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = Clock(3);
            p.Purchases = Buys(30, 15);
        });
        var cut = RenderDetail(id);

        var panel = CountPanel(cut);
        Assert.Contains($"5 cans on hand · you last counted {Clock(3):MMM d, yyyy}", panel);
        // With a rhythm behind it, the count also projects when it runs dry — the believed form
        // may make that claim; the attributed forms below must not.
        Assert.Contains("that's about enough until", panel);
    }

    [Fact]
    public void A_fresh_count_covering_a_due_rhythm_says_why_the_list_is_quiet_and_relabels_the_rhythm_row()
    {
        var id = Seed(p =>
        {
            p.DefaultUnit = "cans";
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = Clock(3);
            p.Purchases = Buys(30, 15);
        });
        var cut = RenderDetail(id);

        // The suppression note names the date the rhythm would have asked on — safe to call it the
        // rhythm's own date, because suppression stands down whenever anything else (label, signal,
        // stale count) is what made the item due.
        Assert.Contains("Not on the grocery list because of this count — its rhythm would otherwise have asked for it",
            CountPanel(cut));

        // And the rhythm row one section up must not contradict it: "Next buy · overdue" beside a
        // Stocked chip is the self-contradiction shape; under suppression the row says whose date it is.
        var labels = cut.FindAll("dl.rhythm dt").Select(d => d.TextContent.Trim()).ToList();
        Assert.Contains("Rhythm would ask", labels);
        Assert.DoesNotContain("Next buy", labels);
    }

    [Fact]
    public void An_aging_count_is_attributed_not_asserted_with_the_no_rhythm_sentence()
    {
        // §13.8 census stock: counted once, never bought through the app — no rhythm exists, so at
        // 90+ days the count is asked about on AGE alone (without this, the weakest evidence would
        // be trusted forever: no receipt will ever correct it).
        var id = Seed(p =>
        {
            p.DefaultUnit = "cans";
            p.TrackQuantity = true;
            p.QuantityOnHand = 12m;
            p.QuantityCountedAt = Clock(100);
        });
        var cut = RenderDetail(id);

        var panel = CountPanel(cut);
        Assert.Contains($"You counted 12 cans on {Clock(100):MMM d, yyyy}", panel);
        Assert.Contains("isn't counting on that any more", panel);
        // The Aging sentence, not the Spent one: only one of the two reasons has a rhythm to have outlived.
        Assert.Contains("aren't enough purchases yet", panel);
        Assert.DoesNotContain("older than it should have lasted", panel);
        // No rhythm means Status stays Unknown — the panel must not claim a list membership that
        // doesn't exist (the CountLooksStale-implies-relisted mistake, read from Status instead).
        Assert.DoesNotContain("It's on the grocery list again.", panel);
        // And no projection: attributing is the honest floor — elapsed time says nothing about
        // how much got eaten, so no "enough until" claim may survive the downgrade.
        Assert.DoesNotContain("that's about enough until", panel);
    }

    [Fact]
    public void A_spent_count_gets_the_outlived_sentence_and_the_row_returns_to_the_list()
    {
        // A rhythm exists (15-day cadence, one package per trip), so a count of 2 should have been
        // gone in ~30 days; at 100 days it stopped holding anything back.
        var id = Seed(p =>
        {
            p.DefaultUnit = "cans";
            p.TrackQuantity = true;
            p.QuantityOnHand = 2m;
            p.QuantityCountedAt = Clock(100);
            p.Purchases = Buys(140, 125);
        });
        var cut = RenderDetail(id);

        var panel = CountPanel(cut);
        Assert.Contains($"You counted 2 cans on {Clock(100):MMM d, yyyy}", panel);
        Assert.Contains("older than it should have lasted", panel);
        Assert.DoesNotContain("aren't enough purchases yet", panel);
        // Here the consequence IS real — the rhythm is long overdue — and Status says so.
        Assert.Contains("It's on the grocery list again.", panel);

        // The other side of the rhythm-row relabel: a stale count doesn't suppress, so the row
        // keeps its ordinary "Next buy" label — only an ACTIVE suppression may rename the date.
        var labels = cut.FindAll("dl.rhythm dt").Select(d => d.TextContent.Trim()).ToList();
        Assert.Contains("Next buy", labels);
        Assert.DoesNotContain("Rhythm would ask", labels);
    }

    [Fact]
    public void A_distrusted_zero_reads_as_unknown_not_as_maybe_some()
    {
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 0m;
            p.QuantityCountedAt = Clock(100);
        });
        var cut = RenderDetail(id);

        // An aged zero means nobody has looked since — unknown, not optimistic. "Isn't counting on
        // that any more" about a zero would read as the app suspecting there might be some.
        var panel = CountPanel(cut);
        Assert.Contains($"You recorded none on {Clock(100):MMM d, yyyy}", panel);
        Assert.Contains("treats it as unknown now", panel);
        Assert.DoesNotContain("isn't counting on that any more", panel);
    }

    // ------------------------------------------------------------------------------ the writes

    [Fact]
    public async Task An_empty_count_box_is_refused_not_read_as_zero()
    {
        var id = Seed(p => p.Purchases = Buys(30, 15));
        var cut = RenderDetail(id);

        // editQuantity starts null for a never-counted product, so a stray click on "Set count"
        // must refuse — coercing null to 0 would assert an outage from a field nobody typed in
        // (§13.4: nothing may mint an OutNow the household didn't give).
        Click(cut, "Set count");

        cut.WaitForAssertion(() => Assert.Equal(
            "Type how many you have. (An empty box isn't zero — to say you're out, enter 0.)",
            cut.Find("p.error").TextContent.Trim()));

        var product = await LoadAsync(id);
        Assert.False(product.TrackQuantity);
        Assert.Null(product.QuantityOnHand);
        Assert.Empty(product.Signals);
    }

    [Fact]
    public async Task A_negative_absolute_count_is_refused_with_its_own_message()
    {
        var id = Seed(p => p.Purchases = Buys(30, 15));
        var cut = RenderDetail(id);

        // min="0" only styles validity — typing a minus sign still binds. Refused, not clamped:
        // a clamp to 0 would file an OutNow off a typo.
        cut.Find("input[aria-label^='How many']").Change("-3");
        Click(cut, "Set count");

        cut.WaitForAssertion(() => Assert.Equal(
            "A count can't be negative. To say you're out, enter 0.",
            cut.Find("p.error").TextContent.Trim()));

        var product = await LoadAsync(id);
        Assert.False(product.TrackQuantity);
        Assert.Null(product.QuantityOnHand);
    }

    [Fact]
    public async Task A_used_one_beaten_by_a_concurrent_stop_counting_explains_the_refusal()
    {
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = Clock(3);
            p.Purchases = Buys(30, 15);
        });
        var cut = RenderDetail(id);

        // Another circuit stops counting under this page's feet — the one road to a false return
        // from the store's relative write. The dormant number must stay frozen.
        using (var db = Db.CreateDbContext())
        {
            db.Products.Single(p => p.Id == id).TrackQuantity = false;
            db.SaveChanges();
        }

        Click(cut, "Used one");

        cut.WaitForAssertion(() => Assert.Equal(
            "Couldn't save — set a starting count before adjusting it up or down.",
            cut.Find("p.error").TextContent.Trim()));

        var product = await LoadAsync(id);
        Assert.Equal(5m, product.QuantityOnHand);
        Assert.Equal(Clock(3), product.QuantityCountedAt);
    }

    [Fact]
    public async Task A_failed_write_on_used_one_advises_retry_and_nothing_landed()
    {
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = Clock(3);
            p.Purchases = Buys(30, 15);
        });
        var cut = RenderDetail(id);

        Factory.FailAfter = 0; // the store's own context dies — the decrement provably never landed
        Click(cut, "Used one");

        cut.WaitForAssertion(() => Assert.Equal(
            "That didn't save. Reload the page and try again.",
            cut.Find("p.error").TextContent.Trim()));

        Factory.FailAfter = null;
        Assert.Equal(5m, (await LoadAsync(id)).QuantityOnHand);
    }

    [Fact]
    public async Task A_failed_reload_after_a_landed_used_one_warns_against_repeating_it()
    {
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = Clock(3);
            p.Purchases = Buys(30, 15);
        });
        var cut = RenderDetail(id);

        // The store's context succeeds; the RELOAD's dies. "Used one" is relative — repeated, it
        // takes a SECOND package — so the advice must invert from the failed-write case.
        Factory.FailAfter = 1;
        Click(cut, "Used one");

        cut.WaitForAssertion(() => Assert.Equal(
            "Saved — but the page couldn't refresh. Reload to see it; don't repeat the change.",
            cut.Find("p.error").TextContent.Trim()));

        Factory.FailAfter = null;
        Assert.Equal(4m, (await LoadAsync(id)).QuantityOnHand);
    }

    [Fact]
    public async Task Stop_counting_goes_dormant_keeping_the_number_and_its_date()
    {
        var id = Seed(p =>
        {
            p.DefaultUnit = "cans";
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = Clock(3);
            p.Purchases = Buys(30, 15);
        });
        var cut = RenderDetail(id);

        Click(cut, "Stop counting");

        // Dormant, not destroyed (the v3.6 toggle semantics): what was known and when is still
        // true and still shown — amnesia would make resuming start from a lie.
        cut.WaitForAssertion(() => Assert.Contains(
            $"Not counted any more — you counted 5 cans on {Clock(3):MMM d, yyyy}. Enter a number to start again.",
            CountPanel(cut)));

        var product = await LoadAsync(id);
        Assert.False(product.TrackQuantity);
        Assert.Equal(5m, product.QuantityOnHand);
        Assert.Equal(Clock(3), product.QuantityCountedAt);
    }

    [Fact]
    public async Task The_unit_box_relabels_every_quantity_without_touching_the_number()
    {
        var id = Seed(p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = Clock(3);
            p.Purchases = Buys(30, 15);
        });
        var cut = RenderDetail(id);
        Assert.Contains("5 on hand", CountPanel(cut)); // unlabeled before

        cut.Find("input[aria-label^='Display unit']").Change("jars");
        Click(cut, "Set unit");

        cut.WaitForAssertion(() => Assert.Contains("5 jars on hand", CountPanel(cut)));

        var product = await LoadAsync(id);
        Assert.Equal("jars", product.DefaultUnit);
        Assert.Equal(5m, product.QuantityOnHand); // display only — the count is a different fact
    }

    // ------------------------------------------------------------------- steering and hygiene

    [Fact]
    public void The_fast_mover_nudge_shows_only_where_counting_would_fight_the_rhythm()
    {
        // An uncounted item bought every ~7 days: a count here is wrong within days and the drift
        // check would then keep asking — steer, never gate.
        var fastId = Seed(p => p.Purchases = Buys(21, 14, 7));
        var fast = RenderDetail(fastId);
        Assert.Contains("hard to keep true", CountPanel(fast));
    }

    [Fact]
    public void The_nudge_stays_silent_for_slow_movers_counted_items_and_census_stock()
    {
        // Three products that must NOT nudge: a slow mover (counting pays off there), an already
        // counted fast mover (the choice is made — nagging it is noise), and a rhythm-less census
        // item (§13.8 is the feature's best case, not a counting mistake).
        var slowId = Seed(p => p.Purchases = Buys(60, 30));
        Assert.DoesNotContain("hard to keep true", CountPanel(RenderDetail(slowId)));

        var countedFastId = Seed(p =>
        {
            p.Name = "Whole Milk";
            p.TrackQuantity = true;
            p.QuantityOnHand = 2m;
            p.QuantityCountedAt = Clock(1);
            p.Purchases = Buys(21, 14, 7);
        });
        Assert.DoesNotContain("hard to keep true", CountPanel(RenderDetail(countedFastId)));

        var censusId = Seed(p => p.Name = "Quarter Cow");
        Assert.DoesNotContain("hard to keep true", CountPanel(RenderDetail(censusId)));
    }

    [Fact]
    public void A_transient_error_clears_on_the_next_successful_reload()
    {
        var id = Seed(p => { p.DefaultUnit = "cans"; p.Purchases = Buys(30, 15); });
        var cut = RenderDetail(id);

        Click(cut, "Set count"); // empty box — provokes the refusal
        cut.WaitForState(() => cut.FindAll("p.error").Count > 0);

        // A successful save reloads the page state; an error stranded from the earlier attempt
        // would otherwise sit beside unrelated, current content.
        cut.Find("input[aria-label^='How many']").Change("4");
        Click(cut, "Set count");

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("p.error"));
            Assert.Contains("4 cans on hand", CountPanel(cut));
        });
    }

    [Fact]
    public void Transient_panels_reset_when_the_page_switches_product()
    {
        // A merge navigates this SAME reused component instance to the target product — a panel
        // left open there would offer a stale candidate list including the product now on screen.
        var aId = Seed(p => p.Name = "Alpha Beans");
        var bId = Seed(p => p.Name = "Beta Beans");
        var cut = RenderDetail(aId);

        cut.Find("button[aria-label^='Merge']").Click();
        cut.WaitForState(() => cut.FindAll(".merge-panel").Count > 0);

        cut.Render(ps => ps.Add(p => p.Id, bId));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Beta Beans", cut.Find("h1").TextContent.Trim());
            Assert.Empty(cut.FindAll(".merge-panel"));
        });
    }
}
