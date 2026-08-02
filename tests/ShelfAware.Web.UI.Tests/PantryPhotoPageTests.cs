using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Census;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The "count from a photo" page (DESIGN.md §13.8) over the REAL census write path on the shared test
/// database, with fakes only at the two browser/AI seams: the photo loader (JS interop bUnit cannot cross)
/// and the reader itself.
/// <para>What these mostly pin is the page's HONESTY contract — which rows arrive ticked, what a row that
/// couldn't be identified does, and that confirming writes counts and never purchases. The grid is the one
/// place a household sees the difference between "the label says fish" and "it looks like fish", so the
/// difference has to survive rendering.</para>
/// </summary>
public class PantryPhotoPageTests : PageTestContext
{
    internal QueueReader Reader = null!;
    internal StubPhotoLoader PhotoLoader = null!;

    /// <summary>Hands back canned reads and records what it was given.</summary>
    internal sealed class QueueReader : IShelfCensusReader
    {
        public Queue<ShelfCensusResult> Results { get; } = new();
        public List<int> PhotoCounts { get; } = [];
        public List<string> ProductHints { get; } = [];

        public Task<ShelfCensusResult> ReadAsync(
            IReadOnlyList<ShelfPhoto> photos, IReadOnlyList<string>? knownProductNames = null,
            CancellationToken cancellationToken = default)
        {
            PhotoCounts.Add(photos.Count);
            if (knownProductNames is not null) ProductHints.AddRange(knownProductNames);
            return Task.FromResult(Results.Count > 0
                ? Results.Dequeue()
                : ShelfCensusResult.Fail("no result queued"));
        }
    }

    /// <summary>Stands in for the browser-side downscale. <c>RequestImageFileAsync</c> reaches into JS and
    /// throws outright under bUnit, so this is what makes the flow beneath it reachable at all.</summary>
    internal sealed class StubPhotoLoader : IShelfPhotoLoader
    {
        public Exception? Throws { get; set; }

        public Task<ShelfPhoto> LoadAsync(IBrowserFile file, CancellationToken cancellationToken = default) =>
            Throws is not null
                ? Task.FromException<ShelfPhoto>(Throws)
                : Task.FromResult(new ShelfPhoto([1, 2, 3], "image/jpeg"));
    }

    protected override void RegisterAdditionalServices()
    {
        Reader = new QueueReader();
        PhotoLoader = new StubPhotoLoader();
        Services.AddSingleton<IShelfCensusReader>(Reader);
        Services.AddSingleton<IShelfPhotoLoader>(PhotoLoader);
        Services.AddSingleton(new CensusConfirmationService(Factory));
    }

    // --- helpers -------------------------------------------------------------

    private static CensusItem Item(
        string name, int count = 1, decimal confidence = 0.9m,
        CensusEvidence evidence = CensusEvidence.Label, string? label = "SOMETHING",
        Category category = Category.Frozen, string? suggested = null) =>
        new()
        {
            NormalizedName = name,
            VisibleCount = count,
            Confidence = confidence,
            Evidence = evidence,
            LabelText = evidence == CensusEvidence.Label ? label : null,
            Category = category,
            SuggestedProductName = suggested,
        };

    private async Task<Product> SeedProduct(
        string name, bool tracked = true, bool counted = false, decimal? onHand = null,
        params DateOnly[] purchases)
    {
        await using var db = Db.CreateDbContext();
        var product = new Product
        {
            Name = name,
            Category = Category.Frozen,
            IsTracked = tracked,
            TrackQuantity = counted,
            QuantityOnHand = onHand,
            QuantityCountedAt = counted ? DateTimeOffset.Now.AddDays(-10) : null,
            Purchases = [.. purchases.Select(d => new PurchaseEvent { PurchasedAt = d, Quantity = 1 })],
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    /// <summary>Select a photo and run the read, leaving the page on the review grid.</summary>
    private IRenderedComponent<PantryPhoto> Review(params CensusItem[] items)
    {
        Reader.Results.Enqueue(ShelfCensusResult.Ok(items, "{}"));
        var cut = Render<PantryPhoto>();
        Upload(cut, 1);
        cut.Find("button").Click();
        return cut;
    }

    private static void Upload(IRenderedComponent<PantryPhoto> cut, int photos) =>
        cut.FindComponent<InputFile>().UploadFiles(
            [.. Enumerable.Range(0, photos).Select(i =>
                InputFileContent.CreateFromBinary([1, 2, 3], $"shelf-{i}.jpg", contentType: "image/jpeg"))]);

    private static IElement RowFor(IRenderedComponent<PantryPhoto> cut, string name) =>
        cut.FindAll("tbody tr").Single(r => r.QuerySelectorAll("input")
            .Any(i => i.GetAttribute("value") == name));

    private static IElement TickOf(IElement row) => row.QuerySelector("input[type=checkbox]")!;

    private static bool IsTicked(IElement row) => TickOf(row).HasAttribute("checked");

    // --- what arrives ticked -------------------------------------------------

    [Fact]
    public void A_confident_read_arrives_ticked_and_an_uncertain_one_does_not()
    {
        // The heart of it: a legible label is ready to confirm, a guess has to be opted into. 0.6 is the
        // SAME threshold the receipt review grid highlights a low-confidence line at.
        var cut = Review(
            Item("Tilapia Fillets", confidence: 0.92m),
            Item("Chicken Breast", confidence: 0.45m, evidence: CensusEvidence.Appearance));

        Assert.True(IsTicked(RowFor(cut, "Tilapia Fillets")));
        Assert.False(IsTicked(RowFor(cut, "Chicken Breast")));
    }

    [Fact]
    public void An_unidentified_package_is_shown_unticked_with_its_reason()
    {
        // §13.8's designed-for limit. Reporting a package it cannot name is useful — the household can name
        // it. Inventing a name for it is not, so the row says plainly that it couldn't tell.
        var cut = Review(Item("foil-wrapped parcel", count: 4, confidence: 0.2m,
            evidence: CensusEvidence.Unidentified, category: Category.Other));

        var row = RowFor(cut, "foil-wrapped parcel");
        Assert.False(IsTicked(row));
        Assert.Contains("couldn't tell", Collapsed(row));
        Assert.Contains("Name it if you know what it is", Collapsed(row));
    }

    [Fact]
    public void The_grid_says_whether_it_read_a_label_or_only_looked()
    {
        var cut = Review(
            Item("Tilapia Fillets", label: "TILAPIA FILLETS 16 OZ"),
            Item("Bananas", evidence: CensusEvidence.Appearance, category: Category.Produce));

        var read = Collapsed(RowFor(cut, "Tilapia Fillets"));
        Assert.Contains("read the label", read);
        Assert.Contains("TILAPIA FILLETS 16 OZ", read); // the claim, checkable against the photo
        Assert.Contains("by sight", Collapsed(RowFor(cut, "Bananas")));
    }

    [Fact]
    public void Tick_all_and_untick_all_move_every_row()
    {
        var cut = Review(Item("Tilapia Fillets"), Item("Chicken Breast", confidence: 0.3m));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Tick all")).Click();
        Assert.All(cut.FindAll("tbody tr"), r => Assert.True(IsTicked(r)));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Untick all")).Click();
        Assert.All(cut.FindAll("tbody tr"), r => Assert.False(IsTicked(r)));
    }

    [Fact]
    public void With_nothing_ticked_there_is_nothing_to_confirm()
    {
        var cut = Review(Item("Chicken Breast", confidence: 0.3m));

        var confirm = cut.FindAll("button").Single(b => b.TextContent.Contains("Count "));
        Assert.True(confirm.HasAttribute("disabled"));
        Assert.Contains("Nothing ticked yet", cut.Markup);
    }

    // --- matching ------------------------------------------------------------

    [Fact]
    public async Task A_read_that_names_an_existing_product_pre_selects_it()
    {
        var beef = await SeedProduct("Ground Beef");

        var cut = Review(Item("Ground Beef", suggested: "Ground Beef"));

        var select = RowFor(cut, "Ground Beef").QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"));
        Assert.Equal(beef.Id.ToString(), ((IHtmlSelectElement)select).Value);
    }

    [Fact]
    public async Task An_unidentified_package_is_never_pre_matched_to_a_product()
    {
        // Its name describes a CONTAINER, so matching it would attach a count to a real product on no
        // evidence at all. The collision here is deliberately one ProductMatcher really does make: four
        // frosted parcels in a freezer would otherwise pre-fill the household's box of freezer bags, and
        // confirming that reads "you have 4 boxes of freezer bags" — a count invented out of packaging.
        var bags = await SeedProduct("Freezer Bag");
        Assert.NotNull(ProductMatcher.Resolve("frosted freezer bag", [bags])); // the matcher WOULD bite

        var cut = Review(Item("frosted freezer bag", count: 4, confidence: 0.2m,
            evidence: CensusEvidence.Unidentified, category: Category.Other));

        var select = RowFor(cut, "frosted freezer bag").QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"));
        Assert.Equal("0", ((IHtmlSelectElement)select).Value); // ➕ create new
    }

    [Fact]
    public async Task An_existing_count_is_shown_beside_the_new_one()
    {
        // Attesting REPLACES the number, so the one being replaced belongs on screen — otherwise a recount
        // silently overwrites a number the household may have wanted to keep.
        await SeedProduct("Black Beans", counted: true, onHand: 3);

        var cut = Review(Item("Black Beans", count: 9, suggested: "Black Beans"));

        Assert.Contains("Was 3", Collapsed(RowFor(cut, "Black Beans")));
    }

    [Fact]
    public async Task A_fast_moving_item_says_a_count_will_be_hard_to_keep_true()
    {
        // CountingAdvice, on the row rather than only on a product page nobody may open. A whole-fridge
        // census is exactly where someone counts the milk and then blames the drift questions.
        var today = DateOnly.FromDateTime(DateTime.Today);
        await SeedProduct("Whole Milk", purchases: [today.AddDays(-21), today.AddDays(-14), today.AddDays(-7)]);

        var cut = Review(Item("Whole Milk", suggested: "Whole Milk", category: Category.Dairy));

        Assert.Contains("hard to keep true", Collapsed(RowFor(cut, "Whole Milk")));
    }

    // --- confirming ----------------------------------------------------------

    [Fact]
    public async Task Confirming_writes_counts_and_no_purchases()
    {
        var cut = Review(Item("Tilapia Fillets", count: 3), Item("Frozen Peas", count: 2));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Count ")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Counted 2 items", cut.Markup));

        await using var db = Db.CreateDbContext();
        var products = await db.Products.OrderBy(p => p.Name).ToListAsync();
        Assert.Equal(["Frozen Peas", "Tilapia Fillets"], products.Select(p => p.Name));
        Assert.All(products, p => Assert.True(p.TrackQuantity));
        Assert.Equal(2m, products.Single(p => p.Name == "Frozen Peas").QuantityOnHand);
        Assert.Equal(3m, products.Single(p => p.Name == "Tilapia Fillets").QuantityOnHand);
        // ★ §13.8: a census records what you HAVE, never what you bought.
        Assert.Empty(await db.PurchaseEvents.ToListAsync());
    }

    [Fact]
    public async Task An_unticked_row_is_not_recorded()
    {
        var cut = Review(Item("Tilapia Fillets"), Item("Chicken Breast", confidence: 0.3m));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Count ")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Counted 1 item", cut.Markup));

        await using var db = Db.CreateDbContext();
        Assert.Equal(["Tilapia Fillets"], await db.Products.Select(p => p.Name).ToListAsync());
    }

    [Fact]
    public async Task An_edited_count_is_what_gets_recorded()
    {
        // The photo proposes the front row; the human corrects it (§13.8). If the edit didn't survive, the
        // whole review step would be theatre.
        var cut = Review(Item("Tilapia Fillets", count: 3));

        var box = RowFor(cut, "Tilapia Fillets").QuerySelectorAll("input[type=number]").Single();
        box.Change("11");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Count ")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Counted 1 item", cut.Markup));

        await using var db = Db.CreateDbContext();
        Assert.Equal(11m, (await db.Products.SingleAsync()).QuantityOnHand);
    }

    [Fact]
    public void The_done_panel_says_no_purchases_were_recorded()
    {
        var cut = Review(Item("Tilapia Fillets"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Count ")).Click();

        cut.WaitForAssertion(() => Assert.Contains("No purchases were recorded", Collapsed(cut.Markup)));
    }

    [Fact]
    public async Task A_failed_save_says_nothing_was_recorded_and_that_retrying_is_safe()
    {
        // One transaction, so nothing persisted — and a count is a TOTAL, so pressing it again cannot
        // double anything. Both halves of that are in the message because a household that fears a double
        // count will simply not press it again, and then their shelf goes uncounted.
        var cut = Review(Item("Tilapia Fillets"));
        Factory.FailAfter = 0; // armed after the read's loads, so it lands on the confirm's own context

        cut.FindAll("button").Single(b => b.TextContent.Contains("Count ")).Click();

        cut.WaitForAssertion(() =>
        {
            var error = Collapsed(cut.Find("p.error"));
            Assert.Contains("nothing was recorded", error);
            Assert.Contains("re-counting the same numbers is safe", error);
        });
        await using var db = Db.CreateDbContext();
        Assert.Empty(await db.Products.ToListAsync());
    }

    // --- reading -------------------------------------------------------------

    [Fact]
    public void Every_selected_photo_and_the_product_list_reach_the_reader()
    {
        Reader.Results.Enqueue(ShelfCensusResult.Ok([Item("Tilapia Fillets")], "{}"));
        var cut = Render<PantryPhoto>();
        Upload(cut, 3);
        cut.Find("button").Click();

        Assert.Equal([3], Reader.PhotoCounts);
    }

    [Fact]
    public async Task The_catalog_is_offered_to_the_reader_for_matching()
    {
        await SeedProduct("Ground Beef");
        await SeedProduct("Black Beans");

        Review(Item("Ground Beef"));

        Assert.Contains("Ground Beef", Reader.ProductHints);
        Assert.Contains("Black Beans", Reader.ProductHints);
    }

    [Fact]
    public void A_read_that_finds_nothing_says_so_rather_than_showing_an_empty_grid()
    {
        var cut = Review();

        Assert.Contains("Nothing recognisable turned up", Collapsed(cut.Markup));
        Assert.Empty(cut.FindAll("tbody tr"));
    }

    [Fact]
    public void A_failed_read_offers_another_go()
    {
        Reader.Results.Enqueue(ShelfCensusResult.Fail("the model was unreachable"));
        var cut = Render<PantryPhoto>();
        Upload(cut, 1);

        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("couldn't be read", Collapsed(cut.Find("p.error"))));
        Assert.NotEmpty(cut.FindComponents<InputFile>()); // still able to pick another photo
    }

    [Fact]
    public void A_photo_the_browser_could_not_read_fails_this_page_not_the_circuit()
    {
        PhotoLoader.Throws = new InvalidOperationException("the browser could not decode this image");
        var cut = Render<PantryPhoto>();
        Upload(cut, 1);

        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("something went wrong reading those photos", Collapsed(cut.Find("p.error"))));
    }

    [Fact]
    public void Start_over_returns_to_the_photo_picker()
    {
        var cut = Review(Item("Tilapia Fillets"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Start over")).Click();

        Assert.Empty(cut.FindAll("tbody tr"));
        Assert.NotEmpty(cut.FindComponents<InputFile>());
    }
}
