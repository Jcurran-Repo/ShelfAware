using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Reports page over the real engine and fact loader — presets from the URL, the report card's
/// arithmetic, the custom builder gated by <c>ReportSpecRules</c>, saved reports, and the one
/// subtle cache rule the page itself documents with a ⚠️: a pantry change must recompute the
/// custom RESULT without re-seeding the builder FORM the user may be mid-edit in.
/// Rendering these presets also drives the chart family (ChartLegend/BarChart/TimeSeriesChart/
/// ReportDataTable) against real results.
/// </summary>
public class ReportsPageTests : PageTestContext
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    protected override void RegisterAdditionalServices() =>
        Services.AddSingleton(new ReportDataService(Factory));

    /// <summary>One PRICED purchase: receipt + line + provenance-stamped purchase, the shape the
    /// fact loader prices from.</summary>
    private int SeedPriced(string name, DateOnly date, decimal price, Category category = Category.Pantry,
        decimal quantity = 1m, DateOnly? expires = null, string? tag = null)
    {
        using var db = Db.CreateDbContext();
        var product = db.Products.FirstOrDefault(p => p.Name == name);
        if (product is null)
        {
            product = new Product { Name = name, Category = category };
            if (tag is not null) product.Tags = [new ProductTag { Value = tag }];
            db.Products.Add(product);
            db.SaveChanges();
        }
        var receipt = new Receipt
        {
            Merchant = "Store", PurchasedAt = date, Status = ReceiptStatus.Confirmed, ImagePath = "n/a",
            Lines = [new ReceiptLine
            {
                RawText = name, NormalizedName = name, Quantity = quantity, UnitPrice = price, ProductId = product.Id,
            }],
        };
        db.Receipts.Add(receipt);
        db.SaveChanges();
        db.PurchaseEvents.Add(new PurchaseEvent
        {
            ProductId = product.Id, PurchasedAt = date, Quantity = quantity, ReceiptId = receipt.Id,
            ExpirationDate = expires,
        });
        db.SaveChanges();
        return product.Id;
    }

    private IRenderedComponent<Reports> RenderReports(string? preset = null)
    {
        if (preset is not null)
            Services.GetRequiredService<NavigationManager>().NavigateTo($"/reports?preset={preset}");
        var cut = Render<Reports>();
        cut.WaitForState(() => cut.FindAll(".report-presets").Count > 0);
        return cut;
    }

    private static AngleSharp.Dom.IElement Builder(IRenderedComponent<Reports> cut, string label) =>
        cut.FindAll(".builder-grid label").Single(l => l.TextContent.TrimStart().StartsWith(label))
            .QuerySelector("select,input")!;

    [Fact]
    public void No_history_renders_the_teaching_empty_state_not_an_empty_chart()
    {
        var cut = RenderReports();
        Assert.Contains("No purchase history yet", cut.Find(".report-empty").TextContent);
        Assert.Empty(cut.FindAll("svg"));
    }

    [Fact]
    public void The_report_card_sums_the_month_and_charts_spend_by_aisle()
    {
        // Yesterday unless today is the 1st — the second trip must sit inside BOTH this calendar
        // month and the top-items 30-day window, whatever today's date is.
        var secondTrip = Today.Day >= 2 ? Today.AddDays(-1) : Today;
        SeedPriced("Whole Milk", Today, 6.00m, Category.Dairy);
        SeedPriced("Canned Beans", secondTrip, 4.00m, Category.Pantry);
        var cut = RenderReports();

        // Stats are arithmetic over the fixture: $10 this month, distinct trip dates counted.
        var stats = cut.FindAll(".portfolio .stat").Select(s => s.TextContent).ToList();
        Assert.Contains(stats, s => s.Contains("Spend") && s.Contains((10m).ToString("C0")));
        Assert.Contains(stats, s => s.Contains("Shopping trips") &&
            s.Contains(secondTrip == Today ? "1" : "2"));

        // The aisle chart renders with its legend and the always-present data table beneath —
        // the table is the reader surface and what makes print a document.
        var aisle = cut.FindAll(".report-section").Single(s => s.TextContent.Contains("Spend by aisle"));
        Assert.NotNull(aisle.QuerySelector("svg"));
        Assert.Contains("Dairy", aisle.TextContent);
        Assert.NotNull(aisle.QuerySelector("table"));

        // Top items rank by spend with product links and share bars.
        var top = cut.FindAll(".report-section").Single(s => s.TextContent.Contains("Top items"));
        var rows = top.QuerySelectorAll("tbody tr").ToList();
        Assert.Contains("Whole Milk", rows[0].TextContent); // $6 outranks $4
        Assert.NotNull(rows[0].QuerySelector("a"));
        Assert.Equal(2, top.QuerySelectorAll(".rank-bar").Length);
    }

    [Fact]
    public void The_preset_comes_from_the_url_and_nonsense_falls_back_to_the_report_card()
    {
        SeedPriced("Whole Milk", Today, 6.00m);
        var gap = RenderReports("gap");
        Assert.Contains("The gap report", gap.Find(".report-section h2").TextContent);

        var bogus = RenderReports("bogus");
        Assert.Contains("This month", bogus.Find(".report-section h2").TextContent);
        // The pill bar marks the real preset as current — a bogus link can't leave zero pills lit.
        Assert.Equal("Monthly report card", bogus.Find(".report-preset-pill.active").TextContent.Trim());
    }

    [Fact]
    public void The_builder_refuses_a_dishonest_spec_and_says_why()
    {
        SeedPriced("Whole Milk", Today, 6.00m);
        var cut = RenderReports("custom");

        // Quantity summed across every product is the chart-honesty rule ReportSpecRules exists
        // for: pounds of beef + cans of beans is not a number. The page must surface the
        // objection AND hold the Run button — same rule object the engine throws on.
        Builder(cut, "Measure").Change("Quantity");

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll(".builder-problems li"));
            Assert.True(cut.FindAll(".builder-actions button")
                .Single(b => b.TextContent.Trim() == "Run report").HasAttribute("disabled"));
        });

        // Narrowing to one product answers the objection and frees the button.
        Builder(cut, "Product (optional)").Change(
            cut.FindAll("label select option").First(o => o.TextContent == "Whole Milk").GetAttribute("value"));
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".builder-problems li"));
            Assert.False(cut.FindAll(".builder-actions button")
                .Single(b => b.TextContent.Trim() == "Run report").HasAttribute("disabled"));
        });
    }

    [Fact]
    public void Running_a_report_renders_it_and_writes_the_spec_into_the_url()
    {
        SeedPriced("Whole Milk", Today.AddDays(-5), 6.00m);
        var cut = RenderReports("custom");

        cut.FindAll(".builder-actions button").Single(b => b.TextContent.Trim() == "Run report").Click();

        cut.WaitForAssertion(() =>
        {
            var section = cut.FindAll(".report-section").Single(s => s.QuerySelector("h2")?.TextContent == "Spend");
            Assert.Contains((6.00m).ToString("C"), section.TextContent);
            Assert.NotNull(section.QuerySelector("table")); // the data table rides along
        });

        // The URL is THE spec serialization — the same string a saved report stores and a chat
        // deep link carries; running a report makes it linkable.
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.Contains("preset=custom", nav.Uri);
        Assert.Contains("from=", nav.Uri);
    }

    [Fact]
    public async Task A_deep_link_seeds_the_builder_and_runs_without_a_click()
    {
        SeedPriced("Whole Milk", Today.AddDays(-5), 6.00m);
        var first = RenderReports("custom");
        first.FindAll(".builder-actions button").Single(b => b.TextContent.Trim() == "Run report").Click();
        first.WaitForState(() => first.FindAll(".report-section h2").Any(h => h.TextContent == "Spend"));
        var link = Services.GetRequiredService<NavigationManager>().Uri;
        await DisposeComponentsAsync();

        // A fresh visit to the same URL — the saved-report pill's exact road — must arrive with
        // the form seeded and the result already on screen.
        Services.GetRequiredService<NavigationManager>().NavigateTo(link);
        var second = Render<Reports>();
        second.WaitForAssertion(() =>
        {
            var section = second.FindAll(".report-section").Single(s => s.QuerySelector("h2")?.TextContent == "Spend");
            Assert.Contains((6.00m).ToString("C"), section.TextContent);
        });
    }

    [Fact]
    public async Task Saving_a_report_persists_its_query_and_delete_removes_it()
    {
        SeedPriced("Whole Milk", Today.AddDays(-5), 6.00m);
        var cut = RenderReports("custom");
        cut.FindAll(".builder-actions button").Single(b => b.TextContent.Trim() == "Run report").Click();
        cut.WaitForState(() => cut.FindAll(".builder-save-name").Count == 1);

        cut.Find(".builder-save-name").Change("Milk money");
        cut.FindAll(".builder-actions button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Saved “Milk money”.", cut.Markup);
            Assert.Contains("Milk money", cut.Find(".saved-reports").TextContent);
        });
        await using (var raw = Db.CreateUnscopedContext())
        {
            var saved = Assert.Single(await raw.SavedReports.IgnoreQueryFilters().ToListAsync());
            Assert.Equal("Milk money", saved.Name);
            Assert.Contains("from=", saved.Query); // the pill IS the URL spec
        }

        cut.Find(".saved-report-delete").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".saved-reports")));
        await using (var raw2 = Db.CreateUnscopedContext())
        {
            Assert.Empty(await raw2.SavedReports.IgnoreQueryFilters().ToListAsync());
        }
    }

    [Fact]
    public async Task A_pantry_change_recomputes_the_result_without_reseeding_the_form()
    {
        // The page's own ⚠️ comment: every other preset invalidates by emptiness, but custom's
        // guard keys on the URL — which a pantry change doesn't touch. Clearing customResult is
        // the invalidation, and the recompute must run the OLD spec against the NEW facts without
        // overwriting whatever the user has typed into the form since.
        SeedPriced("Whole Milk", Today.AddDays(-5), 6.00m);
        var cut = RenderReports("custom");
        cut.FindAll(".builder-actions button").Single(b => b.TextContent.Trim() == "Run report").Click();
        cut.WaitForState(() => cut.FindAll(".report-section h2").Any(h => h.TextContent == "Spend"));

        // The user starts re-tuning the form but has NOT run it.
        Builder(cut, "Measure").Change("PurchaseCount");

        // Meanwhile the pantry changes under the page (a voice confirm, another tab).
        SeedPriced("Whole Milk", Today.AddDays(-2), 4.00m);
        await Coordinator.NotifyPantryChangedAsync();

        cut.WaitForAssertion(() =>
        {
            // The RESULT is fresh ($10 now) and still the RUN spec's metric (Spend)…
            var section = cut.FindAll(".report-section").Single(s => s.QuerySelector("h2")?.TextContent == "Spend");
            Assert.Contains((10.00m).ToString("C"), section.TextContent);
        });
        // …while the form keeps the user's unsubmitted edit instead of snapping back to the URL.
        Assert.Equal("PurchaseCount", Builder(cut, "Measure").GetAttribute("value"));
    }

    [Fact]
    public void Waste_watch_is_gated_on_the_toggle_and_reads_evidence_not_verdicts()
    {
        SeedPriced("Whole Milk", Today.AddDays(-20), 6.00m, Category.Dairy,
            expires: Today.AddDays(-10)); // the label passed with no sign it was finished
        var off = RenderReports("waste");
        Assert.Contains("expiration tracking is switched off", off.Find(".report-section").TextContent);

        AppSettings.SetAsync(SettingKeys.TrackExpirationDates, "true").GetAwaiter().GetResult();
        var on = RenderReports("waste");

        // "Worth checking, $ at stake" — never "wasted": the app can't see inside the fridge.
        var section = on.Find(".report-section");
        Assert.Contains("Worth checking", section.TextContent);
        Assert.Contains("1 item", section.TextContent);
        Assert.Contains((6.00m).ToString("C"), section.TextContent);
        Assert.Contains("Whole Milk", section.QuerySelector("tbody")!.TextContent);
    }

    [Fact]
    public void Piling_up_asks_the_engine_and_discloses_thin_outage_evidence()
    {
        // Bought on a steady 20-day rhythm, silent for 50 days, never once marked Out — the
        // backlog's exact target. With 0 of 1 items ever marked out, the "never ran out" half is
        // barely evidence and the report must say so.
        SeedPriced("Canned Beans", Today.AddDays(-90), 2.00m);
        SeedPriced("Canned Beans", Today.AddDays(-70), 2.00m);
        SeedPriced("Canned Beans", Today.AddDays(-50), 2.00m);
        var cut = RenderReports("piling-up");

        var section = cut.Find(".report-section");
        Assert.Contains("Worth checking", section.TextContent);
        var row = section.QuerySelector("tbody tr")!;
        Assert.Contains("Canned Beans", row.TextContent);
        Assert.Contains("days over", row.TextContent);
        Assert.Contains((6.00m).ToString("C"), row.TextContent); // spend across the trips
        Assert.Contains("isn't carrying much weight here", section.TextContent);
    }

    [Fact]
    public void The_eat_preset_charts_logged_meals_and_prices_them_at_todays_receipts()
    {
        SeedPriced("Ground Beef", Today.AddDays(-3), 5.00m, Category.Meat);
        using (var db = Db.CreateDbContext())
        {
            var recipe = new Recipe
            {
                Name = "Chili", SavedAt = DateTimeOffset.Now, EstimatedCaloriesPerServing = 450,
                Ingredients = [new RecipeIngredient { Name = "ground beef", IsMain = true, MatchedProduct = "Ground Beef" }],
            };
            db.Recipes.Add(recipe);
            db.SaveChanges();
            db.MealEvents.Add(new MealEvent { RecipeId = recipe.Id, AteAt = Today });
            db.SaveChanges();
        }
        var cut = RenderReports("eat");

        var sections = cut.FindAll(".report-section");
        var eat = sections.Single(s => s.TextContent.Contains("What we actually eat"));
        Assert.NotNull(eat.QuerySelector("svg"));
        Assert.Contains("Chili", eat.TextContent);

        // Cost per meal: the main priced from the LATEST receipt — "what it costs now", honest
        // about coverage ("1 of 1 main ingredients").
        var costs = sections.Single(s => s.TextContent.Contains("Cost per meal"));
        var row = costs.QuerySelector("tbody tr")!;
        Assert.Contains("Chili", row.TextContent);
        Assert.Contains("1×", row.TextContent);
        Assert.Contains((5.00m).ToString("C"), row.TextContent);
        Assert.Contains("1 of 1 main ingredients", row.TextContent);

        // And the waist preset reads the same meal against the recipe's calorie estimate.
        var waist = RenderReports("waist");
        Assert.Contains("450", waist.FindAll(".portfolio .stat")
            .Single(s => s.TextContent.Contains("This week")).TextContent);
    }

    [Fact]
    public void Price_watch_stays_honest_below_its_floor_but_still_lists_the_movers()
    {
        // One item bought in both halves of the window: a real per-item change, but far below the
        // floor an overall inflation claim needs — the headline must refuse while the row shows.
        SeedPriced("Whole Milk", Today.AddDays(-80), 4.00m);
        SeedPriced("Whole Milk", Today.AddDays(-10), 5.00m);
        var cut = RenderReports("price-watch");

        var section = cut.Find(".report-section");
        Assert.Contains("Not enough repeat purchases yet", section.TextContent);
        var row = section.QuerySelector("tbody tr")!;
        Assert.Contains("Whole Milk", row.TextContent);
        Assert.Contains((4.00m).ToString("C"), row.TextContent);
        Assert.Contains((5.00m).ToString("C"), row.TextContent);
        Assert.Contains("▲ 25%", row.TextContent);
        // The top mover earns its own price-over-time chart.
        Assert.Contains("Whole Milk — what one costs", cut.Markup);
    }

    [Fact]
    public void The_gap_report_names_the_days_lived_without()
    {
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Coffee Beans", Category = Category.Pantry,
                Purchases =
                [
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-40), Quantity = 1m },
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-20), Quantity = 1m },
                    new PurchaseEvent { PurchasedAt = Today.AddDays(-1), Quantity = 1m },
                ],
                Signals =
                [
                    new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = new DateTimeOffset(Today.AddDays(-30).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) },
                    new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = new DateTimeOffset(Today.AddDays(-10).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) },
                ],
            });
            db.SaveChanges();
        }
        var cut = RenderReports("gap");

        var row = cut.Find(".report-section tbody tr");
        Assert.Contains("Coffee Beans", row.TextContent);
        Assert.Contains("~10 days", row.TextContent);    // one lasts (two 10-day burn cycles)
        Assert.Contains("~19.5 days", row.TextContent);  // rebuy median of the 20- and 19-day gaps
        Assert.Contains("out ~9.5 days", row.TextContent); // gap = rebuy − burn, the days lived without
    }
}
