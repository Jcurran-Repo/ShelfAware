using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The grocery list's "Used one" — §13.5's one-tap correction offered WHERE the count is claimed.
/// The rules under test: a suppressed row explains itself through <c>ProductEstimate.CountNote</c>
/// (the one shared phrasing) instead of printing a due date its own Stocked chip would contradict;
/// the tap takes exactly one package (<c>OnePackage</c>, not a hardcoded 1) through the real store
/// WITHOUT renewing the attestation clock (§13.1: only a look re-anchors it — landing at zero is
/// the exception, which IS a look and owes the OutNow); and a tap that loses to a concurrent
/// stop-counting answers with the reloaded corrected row and nothing else — the reload IS the
/// response, and a banner for that race would be noise.
/// </summary>
public class GroceryListUsedOneTests : PageTestContext
{

    /// <summary>Midnight <paramref name="daysAgo"/> days back, offset zero — a fixed instant so the
    /// clock assertions compare against a value the test chose, not one it derived.</summary>
    private static DateTimeOffset Clock(int daysAgo) =>
        new(Today.AddDays(-daysAgo).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>When the seeded count was attested: 3 days ago — fresh, so the engine believes it.</summary>
    private static DateTimeOffset CountedClock => Clock(3);

    /// <summary>A counted product whose rhythm is ASKING: bought 45 and 30 days ago on the learned
    /// 15-day cadence, so the rhythm's own due date is 15 days overdue — which is what gives a fresh
    /// believed count a recommendation to hold back (§13.5: suppression silences a request; a row
    /// nobody was asking for has nothing to suppress).</summary>
    private int SeedCounted(decimal onHand, string unit = "cans", decimal[]? buyQuantities = null)
    {
        var quantities = buyQuantities ?? [1m, 1m];
        using var db = Db.CreateDbContext();
        var product = new Product
        {
            Name = "Canned Beans",
            Category = Category.Pantry,
            DefaultUnit = unit,
            TrackQuantity = true,
            QuantityOnHand = onHand,
            QuantityCountedAt = CountedClock,
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = quantities[0] },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = quantities[1] },
            ],
        };
        db.Products.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    private IRenderedComponent<GroceryList> RenderList()
    {
        var cut = Render<GroceryList>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0);
        return cut;
    }

    /// <summary>The whole visible text of the cell holding the "Used one" control, whitespace
    /// collapsed — so a test can pin what the cell says AND that it says nothing else.</summary>
    private static string NextBuyCellText(IRenderedComponent<GroceryList> cut) =>
        Collapsed(cut.Find("button.linkish").ParentElement!);

    [Fact]
    public void The_suppressed_row_carries_the_count_note_and_an_inline_used_one_not_a_date()
    {
        SeedCounted(3m);
        var cut = RenderList();

        // The control is the inline link-styled action — "linkish" and nothing more. A full button
        // here reads as the row's main act; the correction is a footnote beside the claim it corrects.
        var button = cut.Find("button.linkish");
        Assert.Equal("Used one", button.TextContent.Trim());
        Assert.Equal("linkish", button.GetAttribute("class"));

        // The ENTIRE cell is the note plus the control. Suppression deliberately leaves DueDate on
        // the estimate so surfaces can explain the rhythm, which means a surface that prints it
        // anyway renders "Stocked · 15 days overdue" — a row contradicting itself in one line.
        // Pinning the full text (not just Contains) is what makes a leaked date fail here.
        Assert.Matches(
            $@"^You have 3 cans, counted {CountedClock:MMM d} ?Used one$",
            NextBuyCellText(cut));
    }

    [Fact]
    public async Task Used_one_takes_exactly_one_package_off_and_leaves_the_count_clock_alone()
    {
        // A weight item: fractional purchase quantities (1.2 / 1.3 lb) make OnePackage the 1.25
        // median, so this test fails under a hardcoded "-1" — the amount must be the same "one"
        // that "Ate it" and the product page take.
        var id = SeedCounted(5m, unit: "lb", buyQuantities: [1.2m, 1.3m]);
        var cut = RenderList();
        Assert.Contains($"You have 5 lb, counted {CountedClock:MMM d}", NextBuyCellText(cut));

        cut.Find("button.linkish").Click();

        // The row re-renders from freshly loaded state: new number, SAME counted date — a delta is
        // a statement about what was taken, not a look at the shelf, so it must not renew the
        // count's credibility (§13.1, or the drift check never fires for the most engaged users).
        cut.WaitForAssertion(() =>
            Assert.Contains($"You have 3.75 lb, counted {CountedClock:MMM d}", NextBuyCellText(cut)));

        await using var raw = Db.CreateUnscopedContext();
        var product = await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == id);
        Assert.Equal(3.75m, product.QuantityOnHand);       // 5 − the 1.25 lb median package
        Assert.Equal(CountedClock, product.QuantityCountedAt);
        Assert.Empty(await raw.InventorySignals.IgnoreQueryFilters().ToListAsync()); // above zero: no outage
    }

    [Fact]
    public async Task Taking_the_last_package_records_the_outage_and_the_row_returns_to_the_date_form()
    {
        var id = SeedCounted(1m);
        var cut = RenderList();
        // Fixture check doubles as the singularization surface: 1 of "cans" reads "1 can".
        Assert.Contains($"You have 1 can, counted {CountedClock:MMM d}", NextBuyCellText(cut));

        cut.Find("button.linkish").Click();

        // Zero has nothing to hold back, so suppression lifts and the reloaded row speaks in dates
        // again — the OutNow pins it due TODAY (the outage date), not overdue by the old rhythm.
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("button.linkish"));
            Assert.DoesNotContain("You have", cut.Find("tbody").TextContent);
            var dateCell = cut.FindAll("tbody tr td")[3];
            Assert.Contains($"{Today:MMM d}", dateCell.TextContent);
            Assert.Contains("(today)", dateCell.TextContent);
        });

        await using var raw = Db.CreateUnscopedContext();
        var product = await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == id);
        Assert.Equal(0m, product.QuantityOnHand);
        // §13.4: taking the last package IS seeing the shelf empty — a human's zero, so it writes
        // the outage the burn rhythm learns from, and it re-anchors the clock (the one delta that
        // counts as a look).
        var signal = Assert.Single(await raw.InventorySignals.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SignalKind.OutNow, signal.Kind);
        Assert.Equal(id, signal.ProductId);
        Assert.True(product.QuantityCountedAt > CountedClock);
    }

    [Fact]
    public async Task A_used_one_beaten_by_a_concurrent_stop_counting_reloads_in_silence()
    {
        var id = SeedCounted(3m);
        var cut = RenderList();
        Assert.Contains("You have 3 cans", NextBuyCellText(cut));

        // Another circuit of the same household stops counting under the page's feet — the one
        // road to a false return from the store's relative write.
        using (var db = Db.CreateDbContext())
        {
            var p = db.Products.Single(x => x.Id == id);
            p.TrackQuantity = false;
            db.SaveChanges();
        }

        cut.Find("button.linkish").Click();

        // The reload IS the response: the corrected row renders the rhythm's own state (a date,
        // not a count claim), and no banner appears — a warning about a race the reload already
        // resolved would be noise, not news.
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("button.linkish"));
            var dateCell = cut.FindAll("tbody tr td")[3];
            Assert.Contains($"{Today.AddDays(-15):MMM d}", dateCell.TextContent);
            Assert.Contains("(15 days overdue)", dateCell.TextContent);
        });
        Assert.Empty(cut.FindAll(".error"));

        // Dormant means FROZEN: the refused delta must not have edited the historical pair.
        await using var raw = Db.CreateUnscopedContext();
        var product = await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == id);
        Assert.False(product.TrackQuantity);
        Assert.Equal(3m, product.QuantityOnHand);
        Assert.Equal(CountedClock, product.QuantityCountedAt);
    }

    [Fact]
    public void An_unsuppressed_row_shows_the_date_form_and_offers_no_used_one()
    {
        // The gate's other side: same asking rhythm, no count — the row speaks in dates and the
        // correction control does not exist (there is no count for it to correct).
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Whole Milk",
                Category = Category.Dairy,
                Purchases =
                [
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-45) },
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-30) },
                ],
            });
            db.SaveChanges();
        }
        var cut = RenderList();

        Assert.Empty(cut.FindAll("button.linkish"));
        var dateCell = cut.FindAll("tbody tr td")[3];
        Assert.Contains($"{Today.AddDays(-15):MMM d}", dateCell.TextContent);
        Assert.Contains("(15 days overdue)", dateCell.TextContent);
        Assert.DoesNotContain("You have", dateCell.TextContent);
    }
}
