using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Trends page: tickers compare like with like (only the dominant size bucket's prices form a
/// series — $/bag beside $/lime once read as a 3,100% price increase), grocery change semantics
/// (up is red: it costs more), spend summed from receipt prices into calendar windows, and the
/// forecast honoring a fresh count (a suppressed item's next buy steps from when the count runs
/// out, not from a due date the app is telling the user to ignore).
/// </summary>
public class SpendInsightTests : PageTestContext
{

    private IRenderedComponent<SpendInsight> RenderTrends()
    {
        var cut = Render<SpendInsight>();
        cut.WaitForState(() => cut.FindAll(".portfolio").Count > 0);
        return cut;
    }

    /// <summary>A product with one PRICED purchase per (date, price): a receipt carrying the line
    /// and a purchase stamped with that receipt — the exact paid price, not an estimate.</summary>
    private int SeedPriced(string name, params (DateOnly Date, decimal Price, string? Size)[] buys)
    {
        using var db = Db.CreateDbContext();
        var product = new Product { Name = name, Category = Category.Produce };
        db.Products.Add(product);
        db.SaveChanges();
        foreach (var (date, price, size) in buys)
        {
            var receipt = new Receipt
            {
                Merchant = "Store",
                PurchasedAt = date,
                Status = ReceiptStatus.Confirmed,
                ImagePath = "n/a",
                Lines = [new ReceiptLine
                {
                    RawText = name, NormalizedName = name, Quantity = 1m,
                    UnitPrice = price, Size = size, ProductId = product.Id,
                }],
            };
            db.Receipts.Add(receipt);
            db.SaveChanges();
            db.PurchaseEvents.Add(new PurchaseEvent
            {
                ProductId = product.Id, PurchasedAt = date, Quantity = 1m,
                ReceiptId = receipt.Id, Size = size,
            });
            db.SaveChanges();
        }
        return product.Id;
    }

    [Fact]
    public void The_ticker_charts_only_the_dominant_size_and_labels_it()
    {
        // Two bag purchases and a loose lime: the series is the BAG's two prices — the lime's
        // $0.25 in the same series would render a cliff and a four-digit "change".
        SeedPriced("Limes",
            (Today.AddDays(-40), 7.00m, "2 lb bag"),
            (Today.AddDays(-20), 8.00m, "2 lb bag"),
            (Today.AddDays(-5), 0.25m, null));
        var cut = RenderTrends();

        var row = cut.Find("tbody tr");
        Assert.NotNull(row.QuerySelector(".size-chip")); // mixed sizes → the charted size is NAMED
        // Change compares within the bucket: 7.00 → 8.00 = +14.3%, red, pointing up.
        var change = row.QuerySelector(".change-up")!;
        Assert.Contains("▲", change.TextContent);
        Assert.Contains("14.3%", change.TextContent);
        Assert.Contains((8.00m).ToString("C"), row.TextContent); // current = the bucket's latest
    }

    [Fact]
    public void A_price_drop_reads_green_and_a_single_size_product_gets_no_label()
    {
        SeedPriced("Yogurt",
            (Today.AddDays(-30), 4.00m, "32 oz"),
            (Today.AddDays(-10), 3.00m, "32 oz"));
        var cut = RenderTrends();

        var row = cut.Find("tbody tr");
        Assert.Null(row.QuerySelector(".size-chip")); // one size — a label would be noise
        var change = row.QuerySelector(".change-down")!;
        Assert.Contains("▼", change.TextContent);
        Assert.Contains("25%", change.TextContent);
    }

    [Fact]
    public void Spend_windows_sum_the_paid_prices_by_calendar_month_and_year()
    {
        var firstThisMonth = new DateOnly(Today.Year, Today.Month, 1);
        var lastMonthDay = firstThisMonth.AddDays(-1);
        SeedPriced("Whole Milk",
            (Today, 6.00m, null),
            (lastMonthDay, 4.00m, null));
        var cut = RenderTrends();

        // The fixture computes the same calendar windows the page renders — the assertion is the
        // SUMMING and the paid-price valuation, not the calendar.
        var expectedYear = 6.00m + (lastMonthDay.Year == Today.Year ? 4.00m : 0m);
        var stats = cut.FindAll(".portfolio .stat").Select(s => s.TextContent).ToList();
        Assert.Contains(stats, s => s.Contains("This month") && s.Contains((6.00m).ToString("C")));
        Assert.Contains(stats, s => s.Contains("Last month") && s.Contains((4.00m).ToString("C")));
        Assert.Contains(stats, s => s.Contains("This year") && s.Contains(expectedYear.ToString("C")));
    }

    [Fact]
    public void The_forecast_counts_a_rhythm_that_will_ask_next_month()
    {
        // Overdue 15-day rhythm: wherever today falls in the calendar, a 15-day cadence lands at
        // least one buy inside next month's window, so the forecast must be a real number.
        SeedPriced("Coffee Beans",
            (Today.AddDays(-30), 10.00m, null),
            (Today.AddDays(-15), 10.00m, null));
        var cut = RenderTrends();

        var forecast = cut.Find(".stat.forecast .stat-value").TextContent;
        Assert.NotEqual((0m).ToString("C"), forecast);
    }

    [Fact]
    public void A_fresh_count_pushes_the_forecast_past_next_month()
    {
        // The same rhythm, but the household counted 5 on the shelf three days ago: the engine
        // suppresses the ask, and the forecast steps from when the COUNT runs out (~72 days off —
        // past next month's window however the calendar lies). Forecasting from the ignored due
        // date would bill the household for buys the app itself says not to make.
        var id = SeedPriced("Coffee Beans",
            (Today.AddDays(-30), 10.00m, null),
            (Today.AddDays(-15), 10.00m, null));
        using (var db = Db.CreateDbContext())
        {
            var product = db.Products.Single(p => p.Id == id);
            product.TrackQuantity = true;
            product.QuantityOnHand = 5m;
            product.QuantityCountedAt = new DateTimeOffset(Today.AddDays(-3).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            db.SaveChanges();
        }
        var cut = RenderTrends();

        Assert.Equal((0m).ToString("C"), cut.Find(".stat.forecast .stat-value").TextContent.Trim());
    }

    /// <summary>The "listed multiple times" shape: ONE receipt carrying two lines for the same product
    /// and size (a pre-quantity-fix duplicate, or two produce weigh-ins), with a purchase event per
    /// line — exactly the Dentastix data.</summary>
    private int SeedTwoLineReceipt(string name, DateOnly date, string? size, decimal priceA, decimal priceB)
    {
        using var db = Db.CreateDbContext();
        var product = new Product { Name = name, Category = Category.Produce };
        db.Products.Add(product);
        db.SaveChanges();
        ReceiptLine Line(decimal price) => new()
        {
            RawText = name, NormalizedName = name, Quantity = 1m, UnitPrice = price, Size = size, ProductId = product.Id,
        };
        var receipt = new Receipt
        {
            Merchant = "Store", PurchasedAt = date, Status = ReceiptStatus.Confirmed, ImagePath = "n/a",
            Lines = [Line(priceA), Line(priceB)],
        };
        db.Receipts.Add(receipt);
        db.SaveChanges();
        db.PurchaseEvents.Add(new PurchaseEvent { ProductId = product.Id, PurchasedAt = date, Quantity = 1m, ReceiptId = receipt.Id, Size = size });
        db.PurchaseEvents.Add(new PurchaseEvent { ProductId = product.Id, PurchasedAt = date, Quantity = 1m, ReceiptId = receipt.Id, Size = size });
        db.SaveChanges();
        return product.Id;
    }

    [Fact]
    public void A_single_receipt_that_lists_one_item_twice_is_not_a_price_increase()
    {
        // The Dentastix bug: one shopping trip printed the same 16 oz item on two lines ($36.19 and
        // $48.26). Read as two purchases it showed a phantom ▲33% here, while the product page — which
        // averages the receipt — showed nothing, two screens disagreeing about one price. It is ONE
        // trip: no change to report on either screen.
        SeedTwoLineReceipt("Dog Treats", Today.AddDays(-3), "16 oz", 36.19m, 48.26m);
        var cut = RenderTrends();

        var row = cut.Find("tbody tr");
        Assert.Contains("Dog Treats", row.TextContent);
        Assert.Null(row.QuerySelector(".change-up"));   // no phantom increase…
        Assert.Null(row.QuerySelector(".change-down"));  // …and no change at all — it's one trip
        Assert.DoesNotContain("▲", row.TextContent);
    }

    [Fact]
    public void A_product_whose_only_price_is_zero_has_no_series_and_does_not_crash_the_ticker()
    {
        // A $0.00 line is a coupon/void/misread, not a price. A product with ONLY such lines has no
        // price series at all — PriceSeries.Dominant returns null — so it must be skipped, never left
        // as a null in the lookup that the ticker loop then dereferences (which would 500 the page).
        SeedPriced("Free Sample", (Today.AddDays(-10), 0m, null));
        SeedPriced("Real Yogurt", (Today.AddDays(-20), 4.00m, "32 oz"), (Today.AddDays(-5), 4.50m, "32 oz"));
        var cut = RenderTrends();

        // The page rendered at all (a null series would have thrown here), the priced product shows…
        Assert.Contains("Real Yogurt", cut.Find("tbody").TextContent);
        // …and the all-$0 product has no ticker row.
        Assert.DoesNotContain("Free Sample", cut.Find("tbody").TextContent);
    }

    [Fact]
    public void No_price_history_says_so_instead_of_an_empty_table()
    {
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product { Name = "Unpriced Thing", Category = Category.Pantry });
            db.SaveChanges();
        }
        var cut = RenderTrends();

        Assert.Contains("No price history yet.", cut.Markup);
        Assert.Empty(cut.FindAll("tbody tr"));
    }
}
