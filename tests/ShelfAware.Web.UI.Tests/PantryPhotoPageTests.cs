using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
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

        /// <summary>When set, the next read parks here — a real vision call takes seconds, and an instant
        /// fake would make the in-flight branch untestable. One-shot: consumed by the read it holds.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public async Task<ShelfCensusResult> ReadAsync(
            IReadOnlyList<ShelfPhoto> photos, IReadOnlyList<string>? knownProductNames = null,
            CancellationToken cancellationToken = default)
        {
            PhotoCounts.Add(photos.Count);
            if (knownProductNames is not null) ProductHints.AddRange(knownProductNames);
            if (Hold is { } gate)
            {
                Hold = null;
                await gate.Task;
            }
            return Results.Count > 0 ? Results.Dequeue() : ShelfCensusResult.Fail("no result queued");
        }
    }

    /// <summary>Stands in for the browser-side downscale. <c>RequestImageFileAsync</c> reaches into JS and
    /// throws outright under bUnit, so this is what makes the flow beneath it reachable at all.</summary>
    internal sealed class StubPhotoLoader : IShelfPhotoLoader
    {
        public Exception? Throws { get; set; }

        /// <summary>When set, the next load parks here — so a test can interleave something (a
        /// navigate-away, a second click) with a read that is genuinely mid-flight. One-shot, same
        /// shape as QueueReader.Hold.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public async Task<ShelfPhoto> LoadAsync(IBrowserFile file, CancellationToken cancellationToken = default)
        {
            if (Hold is { } gate)
            {
                Hold = null;
                await gate.Task;
            }
            if (Throws is not null) throw Throws;
            return new ShelfPhoto([1, 2, 3], "image/jpeg");
        }
    }

    internal RecordingLoggerProvider Logs = null!;

    protected override void RegisterAdditionalServices()
    {
        Reader = new QueueReader();
        PhotoLoader = new StubPhotoLoader();
        Logs = new RecordingLoggerProvider();
        Services.AddSingleton<IShelfCensusReader>(Reader);
        Services.AddSingleton<IShelfPhotoLoader>(PhotoLoader);
        Services.AddSingleton<ILoggerProvider>(Logs);
        Services.AddSingleton(new CensusConfirmationService(Factory));
    }

    // --- helpers -------------------------------------------------------------

    private static CensusItem Item(
        string name, int count = 1, decimal confidence = 0.9m,
        CensusEvidence evidence = CensusEvidence.Label, string? label = "SOMETHING",
        Category category = Category.Frozen, string? suggested = null,
        string? variety = null, string? brand = null, string? size = null) =>
        new()
        {
            NormalizedName = name,
            VisibleCount = count,
            Confidence = confidence,
            Evidence = evidence,
            LabelText = evidence == CensusEvidence.Label ? label : null,
            Category = category,
            SuggestedProductName = suggested,
            Variety = variety,
            Brand = brand,
            Size = size,
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
    public void An_unidentified_package_stays_unticked_however_confident_the_reader_sounds()
    {
        // ⚠️ The rule above must not rest on the confidence number alone. The reader caps an unidentified
        // item at 0.3, which is below the tick threshold — so a test seeding 0.2 asserts what its own
        // fixture guarantees and would survive deleting every Unidentified branch on the page. The tick is
        // what authorizes a WRITE, so the page holds the rule itself: raise that cap in the other
        // assembly, or register a different reader, and rows literally named after packaging would
        // otherwise arrive pre-ticked and become real products.
        var cut = Review(
            Item("frosted freezer bag", confidence: 0.99m, evidence: CensusEvidence.Unidentified),
            Item("Tilapia Fillets", confidence: 0.99m));

        Assert.False(IsTicked(RowFor(cut, "frosted freezer bag")));
        Assert.True(IsTicked(RowFor(cut, "Tilapia Fillets"))); // the threshold itself still works
    }

    [Fact]
    public async Task A_match_made_by_name_similarity_is_flagged_and_not_pre_ticked()
    {
        // Confidence measures certainty in the ITEM — how sure the reader is this is peanut butter — and
        // says nothing about WHICH product a name-similarity pass then picked. ProductMatcher's substring
        // rule lands "Peanut Butter" on a catalog's "Butter", and attesting REPLACES that product's count
        // with no undo. So a flawless read must not pre-authorize an unscored guess at the target.
        await SeedProduct("Butter", counted: true, onHand: 3);

        var cut = Review(Item("Peanut Butter", confidence: 0.95m));

        var row = RowFor(cut, "Peanut Butter");
        Assert.False(IsTicked(row));
        Assert.Contains("Matched by name similarity", Collapsed(row));
    }

    [Fact]
    public async Task An_exact_match_is_not_treated_as_a_guess()
    {
        // The complement, and what stops the rule above from unticking everything: a row whose name IS the
        // product's name was not guessed at, so it keeps the ordinary confidence-based tick.
        await SeedProduct("Tilapia Fillets", counted: true, onHand: 3);

        var cut = Review(Item("Tilapia Fillets", confidence: 0.95m));

        var row = RowFor(cut, "Tilapia Fillets");
        Assert.True(IsTicked(row));
        Assert.DoesNotContain("Matched by name similarity", Collapsed(row));
    }

    [Fact]
    public async Task A_name_differing_only_in_PUNCTUATION_is_an_identity_not_a_guess()
    {
        // ⚠️ ProductMatcher folds punctuation to spaces before its exact rule, so this is rule 1 — an
        // identity. Re-deriving exactness in the page by comparing raw strings called it a guess: the row
        // arrived unticked saying "not read off the package" one cell from a chip reading "read the
        // label", about a match the matcher had already called exact. The seeded catalog contains such a
        // name, so this is the shipped demo's own case.
        await SeedProduct("Home-Canned Tomato Sauce", counted: true, onHand: 4);

        var cut = Review(Item("Home Canned Tomato Sauce", confidence: 0.95m));

        var row = RowFor(cut, "Home Canned Tomato Sauce");
        Assert.True(IsTicked(row));
        Assert.DoesNotContain("Matched by name similarity", Collapsed(row));
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
    public async Task Tick_all_leaves_unidentified_and_similarity_rows_for_a_human()
    {
        // §13.8 licenses Tick all against the CONFIDENCE default only — the other two tick
        // conditions answer different questions (what the name means; WHICH product gets
        // overwritten), and one click must not opt into thirty of them. The unidentified row sits
        // at 0.99 so the skip can't be the threshold's doing, and the low-confidence Label row IS
        // ticked — the licensed override, in the same fixture, so the three-way difference is
        // pinned in one place.
        await SeedProduct("Butter", counted: true, onHand: 9);

        var cut = Review(
            Item("frosted freezer bag", confidence: 0.99m, evidence: CensusEvidence.Unidentified),
            Item("Peanut Butter", confidence: 0.95m),  // similarity-matches the seeded Butter
            Item("Chicken Breast", confidence: 0.3m)); // uncertain, but honestly read

        cut.FindAll("button").Single(b => b.TextContent.Contains("Tick all")).Click();

        Assert.False(IsTicked(RowFor(cut, "frosted freezer bag")));
        Assert.False(IsTicked(RowFor(cut, "Peanut Butter")));
        Assert.True(IsTicked(RowFor(cut, "Chicken Breast")));
    }

    [Fact]
    public async Task Tick_all_ticks_a_similarity_row_the_human_already_resolved()
    {
        // The guard is about the match still being a GUESS. Once the human has picked — here,
        // turned the row into a create-new — the question Tick all was deferring is answered.
        // Pins the shared FuzzyStillSelected definition against a bare "was ever fuzzy" flag,
        // which would leave a resolved row permanently second-class.
        await SeedProduct("Butter", counted: true, onHand: 9);

        var cut = Review(Item("Peanut Butter", confidence: 0.95m));
        RowFor(cut, "Peanut Butter").QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"))
            .Change("0"); // ➕ create new — the human has looked

        cut.FindAll("button").Single(b => b.TextContent.Contains("Tick all")).Click();

        Assert.True(IsTicked(RowFor(cut, "Peanut Butter")));
    }

    [Fact]
    public void Untick_all_clears_even_rows_a_human_ticked_by_hand()
    {
        // Unticking asserts nothing, so it has no guard — a reset must reset.
        var cut = Review(Item("foil-wrapped parcel", confidence: 0.2m,
            evidence: CensusEvidence.Unidentified, category: Category.Other));
        TickOf(RowFor(cut, "foil-wrapped parcel")).Change(true); // the human opts the guess in
        Assert.True(IsTicked(RowFor(cut, "foil-wrapped parcel")));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Untick all")).Click();

        Assert.False(IsTicked(RowFor(cut, "foil-wrapped parcel")));
    }

    [Fact]
    public void Two_varieties_of_one_item_are_told_apart_on_the_grid()
    {
        // The reader emits a row per variety of one item (rule 7) and matches across varieties
        // (rule 11), so two same-named rows are its ORDINARY output — and the page's own "counted
        // twice" warning primes the household to ✕ an apparent duplicate, which silently shorts
        // the summed total. The metadata must survive to the grid AND to the accessible name: two
        // rows that differ only in a subtitle are still identical to a screen reader.
        var cut = Review(
            Item("Drink Mix", count: 3, variety: "Strawberry", brand: "Kool-Aid"),
            Item("Drink Mix", count: 2, variety: "Grape"));

        var rows = cut.FindAll("tbody tr");
        Assert.Contains(rows, r => Collapsed(r).Contains("Strawberry · Kool-Aid"));
        Assert.Contains(rows, r => Collapsed(r).Contains("Grape"));
        var labels = rows.Select(r => TickOf(r).GetAttribute("aria-label")).ToList();
        Assert.Equal(2, labels.Distinct().Count());
        Assert.Contains(labels, l => l!.Contains("Strawberry"));
    }

    [Fact]
    public async Task A_matched_rows_category_is_the_products_own_and_not_an_editable_guess()
    {
        // The reader's category is consumed only when a row CREATES a product, so an editable
        // select on a matched row showed a category the store never held and dropped every edit
        // silently. The fixture makes the two DISAGREE — reader says Pantry, the product is
        // Frozen — because a fixture where they agree cannot tell the branches apart.
        await SeedProduct("Tilapia Fillets"); // the helper seeds Frozen

        var cut = Review(Item("Tilapia Fillets", category: Category.Pantry, suggested: "Tilapia Fillets"));

        var row = RowFor(cut, "Tilapia Fillets");
        Assert.DoesNotContain(row.QuerySelectorAll("select"),
            s => s.GetAttribute("aria-label")!.StartsWith("Category"));
        Assert.Contains("Frozen", Collapsed(row));
        Assert.DoesNotContain("Pantry", Collapsed(row));
    }

    [Fact]
    public async Task Switching_a_matched_row_to_create_new_brings_the_category_select_back()
    {
        // On a create-new the guess IS what gets written, so the select has to come back — with
        // the reader's guess still in it as the default.
        await SeedProduct("Ground Beef");
        var cut = Review(Item("Ground Beef", category: Category.Meat, suggested: "Ground Beef"));
        Assert.DoesNotContain(RowFor(cut, "Ground Beef").QuerySelectorAll("select"),
            s => s.GetAttribute("aria-label")!.StartsWith("Category"));

        RowFor(cut, "Ground Beef").QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"))
            .Change("0");

        Assert.Contains(RowFor(cut, "Ground Beef").QuerySelectorAll("select"),
            s => s.GetAttribute("aria-label")!.StartsWith("Category"));
    }

    [Fact]
    public async Task A_create_new_rows_category_edit_is_what_the_confirm_writes()
    {
        // The complement that keeps the cell honest end to end: on a creating row the edit is real.
        var cut = Review(Item("Tilapia Fillets", count: 2, category: Category.Frozen));
        RowFor(cut, "Tilapia Fillets").QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Category"))
            .Change(nameof(Category.Meat));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Count ")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Counted 1 item", Collapsed(cut.Markup)));
        await using var db = Db.CreateDbContext();
        Assert.Equal(Category.Meat, (await db.Products.SingleAsync()).Category);
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
    public async Task The_readers_own_match_LEADS_the_fuzzy_matcher()
    {
        // ⚠️ The documented trust order is "the model's own match leads and ProductMatcher backs it up",
        // and a fixture where both agree cannot tell the two branches apart — the first version of this
        // test survived deleting the entire suggestion branch. So the suggestion here names a product the
        // matcher would NOT return for this item name, and the assertion is that the suggestion wins.
        var frozen = await SeedProduct("Tilapia Fillets");
        var chicken = await SeedProduct("Chicken Breast");
        Assert.Equal(chicken.Id, ProductMatcher.Resolve("Chicken Breast", [frozen, chicken])!.Id); // matcher's answer

        var cut = Review(Item("Chicken Breast", suggested: "Tilapia Fillets")); // the reader disagrees

        var select = RowFor(cut, "Chicken Breast").QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"));
        Assert.Equal(frozen.Id.ToString(), ((IHtmlSelectElement)select).Value);
    }

    [Fact]
    public async Task With_no_suggestion_the_fuzzy_matcher_pre_selects()
    {
        var beef = await SeedProduct("Ground Beef");

        var cut = Review(Item("Ground Beef"));

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
    public async Task Twin_products_are_never_pre_filled_and_the_dropdown_tells_them_apart()
    {
        // Two products share a name (no unique index, and the duplicate guard has a real "Add
        // anyway"), and the reader's Distinct() hint list means its suggestion names a NAME — so a
        // First() prefill pre-authorized a write over an arbitrary twin's count. The row waits for a
        // human, says why, and the option labels carry the counts that make the pick possible —
        // MealStock refuses this state and the "Ate it" picker disambiguates by live count; the
        // census now does both.
        await SeedProduct("Ground Beef", counted: true, onHand: 9);
        await SeedProduct("Ground Beef");

        var cut = Review(Item("Ground Beef", count: 4, suggested: "Ground Beef"));

        var row = RowFor(cut, "Ground Beef");
        Assert.False(IsTicked(row));
        var select = row.QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"));
        Assert.Equal("0", ((IHtmlSelectElement)select).Value);
        Assert.Contains("More than one of your products answers to", Collapsed(row));
        Assert.Contains("9 on hand", Collapsed(row));   // the counted twin's option label
        Assert.Contains("not counted", Collapsed(row)); // the other's
    }

    [Fact]
    public async Task Tick_all_skips_a_row_whose_name_two_products_share()
    {
        // No suggestion here, so this is the MATCHER path: rule 1 finds an "exact" match that is
        // really a coin flip between twins, and both the arrival tick and the bulk tick stand down.
        await SeedProduct("Ground Beef", counted: true, onHand: 9);
        await SeedProduct("Ground Beef");
        var cut = Review(Item("Ground Beef", count: 4));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Tick all")).Click();

        Assert.False(IsTicked(RowFor(cut, "Ground Beef")));
    }

    [Fact]
    public async Task A_typed_name_that_only_normalized_matches_says_where_the_count_will_LAND()
    {
        // ⚠️ The grid's two "which product does this row land on?" sentences must agree with the write.
        // With NameClash on raw equality while the confirm resolved by identity, a typed
        // "Half-and-Half" fell past NameClash to NearMatch and promised "leave this to create a
        // separate item" — over a write that REPLACED the existing product's count, with no "Was N"
        // note (it keys on the dropdown) and no refusal. The household was told the opposite of what
        // happened to their 9.
        await SeedProduct("Half and Half", counted: true, onHand: 9);
        var cut = Review(Item("frosted carton", confidence: 0.2m,
            evidence: CensusEvidence.Unidentified, category: Category.Other));

        // "Name it if you know what it is" — the row invites exactly this.
        RowFor(cut, "frosted carton").QuerySelectorAll("input")
            .Single(i => (i.GetAttribute("aria-label") ?? "").StartsWith("Item name")).Change("Half-and-Half");

        var row = Collapsed(RowFor(cut, "Half-and-Half"));
        Assert.Contains("This will go to the existing", row);
        Assert.DoesNotContain("create a separate item", row);
    }

    [Fact]
    public async Task An_ambiguous_SUGGESTION_says_so_instead_of_leaving_the_row_silently_unticked()
    {
        // ⚠️ Found live on ordinary model output: a can read as "Canned Tomato Sauce" whose
        // `existing_product` suggestion was "Home Canned Tomato Sauce" — a name two products answer
        // to. The row was unticked (right) with nothing able to say why (wrong): every live guard
        // judges row.Name, and the ambiguous string is the SUGGESTION. Worse, NearMatch then filled
        // the silence with "or leave this to create a separate item", which is about a different
        // question entirely. The read-time fact is carried onto the row now.
        await SeedProduct("Home-Canned Tomato Sauce", counted: true, onHand: 9);
        await SeedProduct("Home Canned Tomato Sauce");

        var cut = Review(Item("Canned Tomato Sauce", suggested: "Home Canned Tomato Sauce"));

        var row = RowFor(cut, "Canned Tomato Sauce");
        Assert.False(IsTicked(row));
        Assert.Contains("more than one of your products answers to that name", Collapsed(row));
        Assert.Contains("Home Canned Tomato Sauce", Collapsed(row));     // names what it read it as
        Assert.DoesNotContain("create a separate item", Collapsed(row)); // NearMatch stands down
        cut.FindAll("button").Single(b => b.TextContent.Contains("Tick all")).Click();
        Assert.False(IsTicked(RowFor(cut, "Canned Tomato Sauce")));      // and Tick all honors it
    }

    [Fact]
    public async Task Picking_a_product_answers_the_ambiguous_suggestion_and_the_row_ticks()
    {
        // The complement: the guard is about a pick nobody has made, so making one clears it — the
        // same shape as FuzzyStillSelected. Without this the row would be permanently second-class.
        var keeper = await SeedProduct("Home-Canned Tomato Sauce", counted: true, onHand: 9);
        await SeedProduct("Home Canned Tomato Sauce");
        var cut = Review(Item("Canned Tomato Sauce", suggested: "Home Canned Tomato Sauce"));

        RowFor(cut, "Canned Tomato Sauce").QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"))
            .Change(keeper.Id.ToString());

        var row = RowFor(cut, "Canned Tomato Sauce");
        Assert.DoesNotContain("more than one of your products answers to that name", Collapsed(row));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Tick all")).Click();
        Assert.True(IsTicked(RowFor(cut, "Canned Tomato Sauce")));
    }

    [Fact]
    public async Task A_punctuation_twin_is_ambiguous_to_every_guard_not_just_the_arrival_tick()
    {
        // ⚠️ The pair rule 1's own normalization folds together — and the drift the gate caught:
        // the arrival tick judged ambiguity by the matcher while the warning and Tick all judged it
        // by raw names, so this row arrived unticked with NO reason shown and Tick all walked
        // straight through the gap, replacing a real count on the strength of how the vision model
        // happened to punctuate. One definition now (ProductMatcher.ExactMatches), asserted at all
        // three guards in one fixture.
        await SeedProduct("Home-Canned Tomato Sauce", counted: true, onHand: 9);
        await SeedProduct("Home Canned Tomato Sauce", counted: true, onHand: 2);

        var cut = Review(Item("Home Canned Tomato Sauce", count: 4));

        var row = RowFor(cut, "Home Canned Tomato Sauce");
        Assert.False(IsTicked(row));                                                  // arrival
        Assert.Contains("More than one of your products answers to", Collapsed(row)); // the reason shows
        cut.FindAll("button").Single(b => b.TextContent.Contains("Tick all")).Click();
        Assert.False(IsTicked(RowFor(cut, "Home Canned Tomato Sauce")));              // bulk
    }

    [Fact]
    public async Task A_twin_picked_by_hand_is_counted_and_the_other_is_untouched()
    {
        // The whole point of refusing to guess: once the HUMAN picks, the write is ordinary — the
        // chosen twin's count is replaced and its sibling's isn't.
        var a = await SeedProduct("Ground Beef", counted: true, onHand: 9);
        var b = await SeedProduct("Ground Beef", counted: true, onHand: 2);
        var cut = Review(Item("Ground Beef", count: 4, suggested: "Ground Beef"));

        RowFor(cut, "Ground Beef").QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"))
            .Change(b.Id.ToString());
        TickOf(RowFor(cut, "Ground Beef")).Change(true);
        cut.FindAll("button").Single(btn => btn.TextContent.Contains("Count ")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Counted 1 item", Collapsed(cut.Markup)));
        await using var db = Db.CreateDbContext();
        Assert.Equal(4m, (await db.Products.SingleAsync(p => p.Id == b.Id)).QuantityOnHand);
        Assert.Equal(9m, (await db.Products.SingleAsync(p => p.Id == a.Id)).QuantityOnHand);
    }

    [Fact]
    public async Task An_existing_count_is_shown_beside_the_new_one_and_FOLLOWS_the_dropdown()
    {
        // Attesting REPLACES the number, so the one being replaced belongs on screen — otherwise a recount
        // silently overwrites a number the household may have wanted to keep. With one seeded product this
        // couldn't distinguish "the right product's count" from "any count", so there are two here and the
        // test changes the match and watches the note follow.
        await SeedProduct("Black Beans", counted: true, onHand: 3);
        var peas = await SeedProduct("Frozen Peas", counted: true, onHand: 11);

        var cut = Review(Item("Black Beans", count: 9, suggested: "Black Beans"));
        var row = RowFor(cut, "Black Beans");
        Assert.Contains("Was 3", Collapsed(row));
        Assert.DoesNotContain("Was 11", Collapsed(row));

        var select = row.QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"));
        select.Change(peas.Id.ToString());

        var moved = Collapsed(RowFor(cut, "Black Beans"));
        Assert.Contains("Was 11", moved);
        Assert.DoesNotContain("Was 3", moved);
    }

    // --- the two the pre-push gate found ---

    [Fact]
    public async Task Choosing_create_new_on_a_matched_row_warns_that_the_name_is_taken()
    {
        // ⚠️ The grid must not say "new product" while the write replaces an existing one's count. Said
        // BEFORE the confirm, so a name clash is something the household resolves rather than discovers.
        await SeedProduct("Ground Beef", counted: true, onHand: 12);

        var cut = Review(Item("Ground Beef", count: 4, suggested: "Ground Beef"));
        var select = RowFor(cut, "Ground Beef").QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"));
        select.Change("0"); // ➕ create new product

        var warned = Collapsed(RowFor(cut, "Ground Beef"));
        Assert.Contains("already exists", warned);
        Assert.Contains("this row will be skipped", warned);
    }

    [Fact]
    public async Task Choosing_create_new_leaves_the_existing_count_untouched()
    {
        var beef = await SeedProduct("Ground Beef", counted: true, onHand: 12);

        var cut = Review(Item("Ground Beef", count: 4, suggested: "Ground Beef"));
        RowFor(cut, "Ground Beef").QuerySelectorAll("select")
            .Single(s => s.GetAttribute("aria-label")!.StartsWith("Product match"))
            .Change("0");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Count ")).Click();

        cut.WaitForAssertion(() => Assert.Contains("a product with that name already exists", Collapsed(cut.Markup)));
        await using var db = Db.CreateDbContext();
        Assert.Single(await db.Products.ToListAsync());                                  // no twin
        Assert.Equal(12m, (await db.Products.SingleAsync(p => p.Id == beef.Id)).QuantityOnHand); // and no loss
    }

    [Fact]
    public async Task Counting_zero_of_something_new_is_declined_and_explained()
    {
        // The row arrives ticked; seeing an empty shelf and typing 0 is what "fix the numbers" invites.
        // Creating the product and asserting an outage would pin it Overdue on the grocery list forever.
        var cut = Review(Item("Frozen Peas", count: 2));

        RowFor(cut, "Frozen Peas").QuerySelectorAll("input[type=number]").Single().Change("0");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Count ")).Click();

        cut.WaitForAssertion(() => Assert.Contains("isn't something Shelf Aware knows about yet", Collapsed(cut.Markup)));
        await using var db = Db.CreateDbContext();
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Empty(await db.InventorySignals.ToListAsync());
        // Nothing was written, so the panel must not open with a tick and "Counted".
        Assert.DoesNotContain("✅", cut.Markup);
        Assert.Contains("Nothing was counted", Collapsed(cut.Markup));
    }

    [Fact]
    public async Task Clearing_the_count_box_refuses_the_row_instead_of_recording_an_outage()
    {
        // ⚠️ The sharpest edge on this page. `@bind` on a non-nullable decimal turns an emptied box into 0,
        // and a ticked 0 is an ASSERTED out — a real OutNow filed against a shelf full of the stuff, from
        // a field nobody typed in, with the blur beating the click so the 0 is never even seen.
        var beans = await SeedProduct("Black Beans", counted: true, onHand: 5,
            purchases: DateOnly.FromDateTime(DateTime.Today.AddDays(-20)));

        var cut = Review(Item("Black Beans", count: 3));
        RowFor(cut, "Black Beans").QuerySelectorAll("input[type=number]").Single().Change("");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Count ")).Click();

        cut.WaitForAssertion(() => Assert.Contains("An empty box isn't a zero", Collapsed(cut.Markup)));
        await using var db = Db.CreateDbContext();
        Assert.Empty(await db.InventorySignals.ToListAsync());
        Assert.Equal(5m, (await db.Products.SingleAsync(p => p.Id == beans.Id)).QuantityOnHand);
    }

    [Fact]
    public void An_emptied_count_box_says_so_on_the_row_before_the_confirm()
    {
        // Refusing it afterwards is the safety net; saying so here is what makes the net unnecessary.
        var cut = Review(Item("Black Beans", count: 3));
        var row = RowFor(cut, "Black Beans");
        Assert.DoesNotContain("Enter how many", Collapsed(row));

        row.QuerySelectorAll("input[type=number]").Single().Change("");

        Assert.Contains("Enter how many", Collapsed(RowFor(cut, "Black Beans")));
    }

    [Fact]
    public void A_negative_count_says_so_on_the_row_before_the_confirm()
    {
        // A negative count is refused at confirm (Unusable) — say so inline beside the empty-count message,
        // rather than only in the Done panel after the click. min="0" is only a browser hint; @bind binds -3.
        var cut = Review(Item("Black Beans", count: 3));
        var row = RowFor(cut, "Black Beans");
        Assert.DoesNotContain("Use zero or more", Collapsed(row));

        row.QuerySelectorAll("input[type=number]").Single().Change("-3");

        Assert.Contains("Use zero or more", Collapsed(RowFor(cut, "Black Beans")));
    }

    [Fact]
    public void A_junk_name_says_it_cannot_name_a_product_before_the_confirm()
    {
        // A name with no letters or digits folds to an empty IdentityKey, so the confirm refuses it
        // (NoName) — but the refusal used to speak only in the Done panel, after the click. Same
        // say-it-before-the-confirm rule as the empty-count and negative-count messages above.
        var cut = Review(Item("Canned Beans"));

        RowFor(cut, "Canned Beans").QuerySelectorAll("input")
            .Single(i => (i.GetAttribute("aria-label") ?? "").StartsWith("Item name")).Change("!!");

        var row = Collapsed(RowFor(cut, "!!"));
        Assert.Contains("no letters or numbers", row);
        Assert.Contains("skipped", row);
    }

    [Fact]
    public void A_blanked_name_asks_for_one_before_the_confirm()
    {
        // The other NoName shape. Blanking the box on an unmatched row never had an inline message
        // either — both shapes gained one together, so the parity is closed rather than shifted.
        var cut = Review(Item("Canned Beans"));

        cut.FindAll("tbody tr").Single().QuerySelectorAll("input")
            .Single(i => (i.GetAttribute("aria-label") ?? "").StartsWith("Item name")).Change("");

        var row = Collapsed(cut.FindAll("tbody tr").Single());
        Assert.Contains("Give this row a name", row);
        Assert.Contains("skipped", row);
    }

    [Fact]
    public async Task A_name_match_that_conflicts_with_an_ambiguous_read_is_unticked_and_says_so()
    {
        // ⚠️ Finding A, end-to-end on the grid. The row's own name identity-matches P1 exactly, but the
        // reader SUGGESTED a twin — so it must NOT auto-tick (that silently attests over P1's count while
        // the twin the reader actually saw gets nothing), and it must say why. The old code returned
        // look:false in this branch and the row auto-ticked at 0.95.
        await SeedProduct("Tomato Sauce");             // P1 — the name match
        await SeedProduct("Home Canned Tomato Sauce"); // the twins the suggestion answers to
        await SeedProduct("Home-Canned Tomato Sauce");
        var cut = Review(Item("Tomato Sauce", confidence: 0.95m, suggested: "Home Canned Tomato Sauce"));

        var row = RowFor(cut, "Tomato Sauce");
        Assert.False(IsTicked(row));                    // not auto-ticked despite 0.95
        Assert.Contains("The name matches your", Collapsed(row));
        Assert.Contains("read as", Collapsed(row));     // surfaces the conflicting read
    }

    [Fact]
    public async Task Renaming_a_row_away_from_an_ambiguous_read_drops_the_stale_warning()
    {
        // ⚠️ Finding C. The reader's suggestion named a twin, so the row arrived unticked explaining "read
        // this as X". That suggestion is frozen at read time — but once the human RENAMES the row to a
        // clean novel name they've taken over its identity, and the warning is about a name it no longer
        // bears (the message even invites "…or leave it to add this as a new item").
        await SeedProduct("Home Canned Sauce");
        await SeedProduct("Home-Canned Sauce"); // an identity twin — the suggestion answers to both
        var cut = Review(Item("Canned Sauce", suggested: "Home Canned Sauce"));

        var row = RowFor(cut, "Canned Sauce");
        Assert.Contains("read this as", Collapsed(row)); // the stale-suggestion warning shows at first

        row.QuerySelector("input[aria-label^='Item name']")!.Change("Marinara Sauce");

        Assert.DoesNotContain("read this as", Collapsed(RowFor(cut, "Marinara Sauce")));
    }

    [Fact]
    public async Task An_ambiguous_suggestion_withholds_the_tick_even_when_the_read_name_has_stray_whitespace()
    {
        // ⚠️ Re-gate finding: EffectiveAmbiguousSuggestion compared Name.Trim() to an UN-trimmed ReadName, so a
        // read name with surrounding whitespace made them unequal AT ARRIVAL (no human edit) — dropping the
        // suggestion and auto-ticking a row finding A withholds. Both sides are trimmed now.
        await SeedProduct("Home Canned Sauce");
        await SeedProduct("Home-Canned Sauce"); // twins the suggestion answers to
        var cut = Review(Item("  Canned Sauce  ", confidence: 0.9m, suggested: "Home Canned Sauce"));

        var row = cut.FindAll("tbody tr").Single();
        Assert.False(IsTicked(row));                    // the ambiguous suggestion still withholds the tick
        Assert.Contains("read this as", Collapsed(row)); // and still explains why
    }

    [Fact]
    public void A_zero_on_a_new_item_says_it_wont_be_recorded_before_the_confirm()
    {
        // ⚠️ Finding D. A confident novel item arrives ticked at 1; typing 0 makes it ZeroOnNewProduct
        // (refused at confirm). The count cell only messages null/negative, and there's no matched product
        // to note — so the row sat ticked with nothing saying it won't be recorded. Said inline now, like
        // the empty-count and negative cases beside it.
        var cut = Review(Item("Frozen Peas", count: 1, confidence: 0.9m));
        var row = RowFor(cut, "Frozen Peas");
        Assert.True(IsTicked(row));                        // arrives ticked at 1
        Assert.DoesNotContain("nothing to record", Collapsed(row));

        row.QuerySelectorAll("input[type=number]").Single().Change("0");

        Assert.Contains("nothing to record", Collapsed(RowFor(cut, "Frozen Peas")));
    }

    [Fact]
    public async Task A_dormant_count_shows_its_stored_number_and_that_counting_will_restart()
    {
        // Stop-counting is dormant, not destructive: the number and date are KEPT as history. A census
        // overwrites both and starts believing them again, so the row cannot look identical to a
        // never-counted one — this is the single state where the grid most owes the household a sentence.
        await SeedProduct("Ground Chuck", counted: false, onHand: 14);
        await using (var seed = Db.CreateDbContext())
        {
            var p = await seed.Products.SingleAsync(x => x.Name == "Ground Chuck");
            p.QuantityCountedAt = DateTimeOffset.Now.AddDays(-90);
            await seed.SaveChangesAsync();
        }

        var cut = Review(Item("Ground Chuck"));

        var row = Collapsed(RowFor(cut, "Ground Chuck"));
        Assert.Contains("Was 14", row);
        Assert.Contains("You'd stopped counting it", row);
    }

    [Fact]
    public async Task An_ALREADY_COUNTED_product_is_not_told_it_had_stopped_counting()
    {
        // ⚠️ The complement of the dormant note, and it was missing: with only the positive case pinned,
        // dropping the `!TrackQuantity` conjunct left every row with a stored number claiming the
        // household had stopped counting it. The "Was N" half must still show — that's the point of the
        // note — while the stopped-counting sentence must not.
        await SeedProduct("Black Beans", counted: true, onHand: 5);

        var cut = Review(Item("Black Beans"));

        var row = Collapsed(RowFor(cut, "Black Beans"));
        Assert.Contains("Was 5", row);
        Assert.DoesNotContain("You'd stopped counting it", row);
    }

    [Fact]
    public async Task Recounting_a_dormant_product_is_reported_in_the_summary()
    {
        // `Retracked` counts a different property (IsTracked), so without its own sentence the one switch
        // the household deliberately turned off would be the one change the summary never mentions.
        await SeedProduct("Ground Chuck", counted: false, onHand: 14);

        var cut = Review(Item("Ground Chuck", count: 3));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Count ")).Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Started counting 1 item you'd stopped counting", Collapsed(cut.Markup)));
    }

    [Fact]
    public async Task An_ignored_product_says_that_counting_it_starts_tracking_again()
    {
        // The same notice, in the same words, the receipt review grid gives for the same situation.
        await SeedProduct("Sparkling Water", tracked: false);

        var cut = Review(Item("Sparkling Water"));

        Assert.Contains("Currently ignored", Collapsed(RowFor(cut, "Sparkling Water")));
    }

    [Fact]
    public async Task A_typed_name_that_resembles_an_existing_product_is_named_before_the_confirm()
    {
        // The standing duplicate guard, where a name is TYPED rather than read. The reader's own match runs
        // once at read time and never sees this name, and "Name it if you know what it is" on an
        // unidentified package invites exactly this — so without the warning a twin gets created silently,
        // splitting purchase history and blinding the predictor.
        await SeedProduct("93% Lean Ground Beef");

        var cut = Review(Item("frosted freezer bag", confidence: 0.2m, evidence: CensusEvidence.Unidentified));
        RowFor(cut, "frosted freezer bag").QuerySelectorAll("input")
            .Single(i => i.GetAttribute("type") is null).Change("Ground Beef");

        Assert.Contains("Looks like your existing", Collapsed(cut.Markup));
        Assert.Contains("93% Lean Ground Beef", Collapsed(cut.Markup));
    }

    [Fact]
    public async Task A_fast_moving_item_that_is_ALREADY_counted_is_not_nudged_about_counting()
    {
        // The nudge is advice about signing UP (the product page gates it on exactly that). Shown for an
        // item the household already counts, it argues against a count they keep — on the very act that
        // refreshes it and answers the drift the warning is about.
        var weekly = Enumerable.Range(1, 6).Select(i => DateOnly.FromDateTime(DateTime.Today.AddDays(-7 * i))).ToArray();
        await SeedProduct("Whole Milk", counted: true, onHand: 2, purchases: weekly);

        var cut = Review(Item("Whole Milk"));

        Assert.DoesNotContain("hard to keep true", Collapsed(RowFor(cut, "Whole Milk")));
    }

    [Fact]
    public async Task A_fast_moving_item_that_is_NOT_counted_still_gets_the_nudge()
    {
        // The complement — the gate must not silence the advice where it belongs.
        var weekly = Enumerable.Range(1, 6).Select(i => DateOnly.FromDateTime(DateTime.Today.AddDays(-7 * i))).ToArray();
        await SeedProduct("Whole Milk", purchases: weekly);

        var cut = Review(Item("Whole Milk"));

        Assert.Contains("hard to keep true", Collapsed(RowFor(cut, "Whole Milk")));
    }

    [Fact]
    public void Removing_every_row_does_not_blame_the_photo()
    {
        // Two ways to reach an empty grid, and only one of them is the photo's fault. Telling someone a
        // perfectly good read "found nothing recognisable" sends them off to re-shoot — and re-spend a
        // vision call on their own key — over a non-problem.
        var cut = Review(Item("Tilapia Fillets"), Item("Bananas"));
        // Re-found each time: removing a row re-renders, which invalidates the other handler ids.
        while (cut.FindAll("tbody button").Count > 0) cut.FindAll("tbody button")[0].Click();

        var text = Collapsed(cut.Markup);
        Assert.DoesNotContain("Nothing recognisable turned up", text);
        Assert.Contains("You've removed everything that was found", text);
    }

    [Fact]
    public void A_read_that_genuinely_finds_nothing_still_says_so()
    {
        // The complement, so the branch above can't swallow the real empty-read message.
        var cut = Review();

        Assert.Contains("Nothing recognisable turned up", Collapsed(cut.Markup));
    }

    [Fact]
    public void A_photo_the_browser_cannot_decode_is_named_rather_than_timing_out()
    {
        // The loader refuses by content type instead of handing an undecodable file to a JS promise that
        // never settles — 30 seconds of spinner followed by a message that never mentions the file.
        // The exact sentence the real loader produces for this file, so a reworded refusal fails here
        // rather than passing against a string this fixture invented. Pinned the other side too:
        // ShelfPhotoLoaderTests asserts the loader really says this.
        PhotoLoader.Throws = new NotSupportedException(
            "“shelf.pdf” is application/pdf, which isn't a photo. Take or pick a picture of the shelf instead.");
        var cut = Render<PantryPhoto>();
        Upload(cut, 1);
        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("isn't a photo", Collapsed(cut.Markup)));
    }

    [Fact]
    public async Task The_confirm_is_NOT_tied_to_the_pages_lifetime_but_the_read_is()
    {
        // ⚠️ A read is re-runnable, so cancelling it when the visitor leaves saves them a vision call. A
        // CONFIRM is a one-shot write over input that exists nowhere else — a census keeps no audit copy
        // and has no Retry — so threading the page's token into it discarded a census the household had
        // already pressed Confirm on: no row written, no message (the component is gone), no log line.
        //
        // Asserted on the TOKEN rather than by racing a detached write to the database. Same guarantee,
        // no timing: an uncancellable token cannot be cancelled by a teardown, and the first version of
        // this test — poll the DB after disposing the component — was flaky precisely because it depended
        // on when a deliberately-detached continuation happened to run.
        await SeedProduct("Black Beans", counted: true, onHand: 1);
        var cut = Review(Item("Black Beans", count: 7));

        Assert.True(Factory.LastToken.CanBeCanceled,
            "the READ's catalog load should carry the page token — leaving mid-read must stop it");

        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Count ")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Counted 1 item", Collapsed(cut.Markup)));

        Assert.False(Factory.LastToken.CanBeCanceled,
            "the CONFIRM must not be cancellable by the page going away — the write has to outlive it");
    }

    [Fact]
    public async Task A_zero_counted_from_a_photo_is_reported_as_running_out()
    {
        // The census's zero is §13.4's evidence like any other — including for §13.8's own population,
        // which has no purchase history by construction (see the service test of the same name for why
        // an attempt to treat that case differently was reverted).
        await SeedProduct("Quarter Cow Ground Beef", counted: true, onHand: 9);

        var cut = Review(Item("Quarter Cow Ground Beef", count: 1));
        RowFor(cut, "Quarter Cow Ground Beef").QuerySelectorAll("input[type=number]").Single().Change("0");
        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Count ")).Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("1 counted at zero — recorded as running out", Collapsed(cut.Markup)));
    }

    [Fact]
    public async Task Leaving_the_page_mid_read_tears_down_quietly_instead_of_logging_an_error()
    {
        // ⚠️ CancellationTokenSource.Token THROWS once its source is disposed (IsCancellationRequested
        // does not), and ObjectDisposedException derives from InvalidOperationException — so it cleared
        // every specific clause the read has and landed in the generic one, writing
        // LogError("Reading a shelf census failed") on an ordinary navigate-away. That is exactly the
        // teardown noise the JSDisconnectedException clause beside it exists to keep out of a real
        // deployment's log. The page captures the token once before the first await; a captured token
        // outlives its source.
        //
        // ⚠️ TWO photos, and the gate on the FIRST, is what makes this observable. The second loop
        // iteration re-reads the token before any other token-aware work, so nothing else can throw
        // first. Parking at the catalog load instead makes the test vacuous: disposal cancels the token,
        // LoadCatalogAsync's own query then throws OperationCanceledException, the `when` clause
        // rethrows, and control never reaches a second token read — an earlier version of this test did
        // exactly that and passed identically with the fix reverted.
        var gate = new TaskCompletionSource();
        Reader.Results.Enqueue(ShelfCensusResult.Ok([Item("Black Beans")], "{}"));
        var cut = Render<PantryPhoto>();
        Upload(cut, 2);
        PhotoLoader.Hold = gate;
        // ⚠️ The awaitable trigger, not Click(): the handler's task completes only when Read()
        // has fully unwound — catches, logging and all — so waiting on it replaces the old
        // Task.Delay(50), which under load could elapse before the continuation ran and pass
        // this NEGATIVE assertion without exercising anything (a wait that cannot fail, item
        // 34's class). The 5s WhenAny is a failsafe against a hang, not a race window: a slow
        // run completes late and still passes; only a genuine hang fails, loudly.
        var handler = cut.Find("button").ClickAsync(new MouseEventArgs());

        await DisposeComponentsAsync();  // the visitor navigates away mid-read
        gate.SetResult();
        var settled = await Task.WhenAny(handler, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(handler, settled);   // the continuation ran to completion — nothing left pending
        try { await handler; } catch (OperationCanceledException) { /* the quiet path itself */ }

        Assert.DoesNotContain(Logs.Errors, e => e.Message.Contains("Reading a shelf census failed"));
    }

    [Fact]
    public void More_than_eight_photos_are_refused_with_the_limit_named()
    {
        // The refusal existed (GetMultipleFiles throws past the cap and the page catches it) but
        // nothing pinned it — a repro-shaped gap: nine phone photos is one over-eager multi-select
        // away, and the failure must name the limit rather than read as a broken page.
        var cut = Render<PantryPhoto>();

        Upload(cut, 9);

        Assert.Contains("up to 8 photos", cut.Markup);
        Assert.Empty(Reader.PhotoCounts); // nothing was read
        // And the recovery is ordinary: a conforming selection clears the error and proceeds.
        Upload(cut, 8);
        Assert.DoesNotContain("up to 8 photos", cut.Markup);
        Assert.Contains("8 photos selected", cut.Markup);
    }

    [Fact]
    public void The_file_input_accepts_any_image_the_browser_can_decode()
    {
        // ⚠️ Pinned because the accept list is load-bearing in a way that reads like decoration: a
        // FORMAT list is what makes iOS transcode HEIC to JPEG, so narrowing it hid real photos behind
        // "can't be read". And naming image/heic here would be worse than either — Safari 17+ then stops
        // transcoding and converts JPEG and PNG INTO HEIC.
        var cut = Render<PantryPhoto>();

        var accept = cut.Find("input[type=file]").GetAttribute("accept");
        Assert.Equal("image/*", accept);
    }

    [Fact]
    public void A_timed_out_read_reports_an_error_instead_of_spinning_forever()
    {
        // ⚠️ Cancellation from OUTSIDE the page is a failure, not teardown: an HttpClient timeout on the
        // vision call arrives as a TaskCanceledException, and rethrowing it looked safe but wasn't —
        // ComponentBase ignores a canceled task, so there was no error, no log line and no final render.
        // Just the spinner, and only a page reload out of it.
        PhotoLoader.Throws = new TaskCanceledException("the call timed out");
        var cut = Render<PantryPhoto>();
        Upload(cut, 1);
        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("took too long", Collapsed(cut.Markup)));
        Assert.DoesNotContain("Looking at your photo", Collapsed(cut.Markup));
    }

    [Fact]
    public void Rolled_up_rows_are_explained_rather_than_looking_dropped()
    {
        // The button counts rows and the result counts products; the reader emits a row per variety but
        // matches across varieties, so the two differ routinely.
        var cut = Review(Item("Drink Mix"), Item("Drink Mix"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Count 2 items")).Click();

        cut.WaitForAssertion(() =>
        {
            var done = Collapsed(cut.Markup);
            Assert.Contains("Counted 1 item", done);
            Assert.Contains("2 rows — some were the same product", done);
        });
    }

    [Fact]
    public async Task The_grid_is_disabled_while_a_confirm_is_in_flight()
    {
        // ⚠️ The confirm snapshots the ticked rows when the button is pressed, so a ✕ or a count edit
        // mid-save would look like it un-counted a row it didn't. The whole grid disables while confirming —
        // the same double-tap discipline the dashboard's quick buttons carry. HoldNext parks the confirm at
        // its context create, exactly the in-flight window a real circuit has.
        var cut = Review(Item("Tilapia Fillets", count: 3), Item("Frozen Peas", count: 2));

        var gate = new TaskCompletionSource();
        Factory.HoldNext = gate;
        cut.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Count ")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.All(cut.FindAll("tbody button"), b => Assert.True(b.HasAttribute("disabled"))); // the ✕
            Assert.All(cut.FindAll("tbody input"), i => Assert.True(i.HasAttribute("disabled")));   // tick + name + count
        });
        gate.SetResult();

        cut.WaitForAssertion(() => Assert.Contains("Counted 2 items", Collapsed(cut.Markup)));
    }

    [Fact]
    public void A_transient_JS_disconnect_mid_read_shows_an_error_not_a_permanent_spinner()
    {
        // ⚠️ A JSDisconnectedException while the page is still ALIVE (a circuit blip during the photo
        // downscale, then a reconnect) must escape the spinner — the empty catch left phase == Reading
        // forever, recoverable only by reload. Real teardown (pageCts cancelled by Dispose) is still
        // swallowed, the same split the OperationCanceledException clause beside it uses.
        PhotoLoader.Throws = new JSDisconnectedException("interop lost mid-read");
        var cut = Render<PantryPhoto>();
        Upload(cut, 1);
        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("connection dropped", Collapsed(cut.Markup)));
        Assert.DoesNotContain("Looking at your photo", Collapsed(cut.Markup)); // not stuck on the spinner
    }

    [Fact]
    public void A_row_removed_with_the_cross_is_not_counted()
    {
        var cut = Review(Item("Tilapia Fillets"), Item("Frozen Peas"));

        RowFor(cut, "Frozen Peas").QuerySelectorAll("button")
            .Single(b => b.GetAttribute("aria-label")!.StartsWith("Remove")).Click();

        Assert.Single(cut.FindAll("tbody tr"));
        Assert.Contains("Count 1 item", cut.Markup);
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
    public void A_second_press_while_reading_does_not_buy_a_second_vision_call()
    {
        // Setting the phase hides the button, but a second click can already be queued before that render
        // reaches the browser — and on a slow circuit it will be. Both would then run: two vision calls
        // billed to the visitor's own key for one press, the second overwriting the first's rows.
        Reader.Results.Enqueue(ShelfCensusResult.Ok([Item("Tilapia Fillets")], "{}"));
        var gate = new TaskCompletionSource();
        Reader.Hold = gate;
        var cut = Render<PantryPhoto>();
        Upload(cut, 1);

        cut.Find("button").Click();  // parks inside the reader
        cut.Find("button").Click();  // must NO-OP
        gate.SetResult();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tbody tr")));
        Assert.Single(Reader.PhotoCounts); // exactly one read
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
