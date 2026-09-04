using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// ProductDetail's edit affordances beyond the count panel: rename (in place — the service
/// re-points name-keyed recipe links, so no reload), merge (the repair path for split history,
/// candidates narrowed by tag), the §13.6 purchase-quantity correction (moves the on-hand count
/// by the DIFFERENCE — fixing history and leaving the shelf wrong just relocates the error),
/// and the "also works as" substitute list.
/// </summary>
public class ProductDetailEditFlowsTests : PageTestContext
{

    private static DateTimeOffset CountedClock =>
        new(Today.AddDays(-3).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private int Seed(string name, Action<Product>? configure = null)
    {
        using var db = Db.CreateDbContext();
        var product = new Product { Name = name, Category = Category.Pantry };
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

    private async Task<Product> LoadAsync(int id)
    {
        await using var raw = Db.CreateUnscopedContext();
        return await raw.Products.IgnoreQueryFilters()
            .Include(p => p.Purchases).Include(p => p.Substitutes)
            .SingleAsync(p => p.Id == id);
    }

    // ------------------------------------------------------------------------------------ rename

    [Fact]
    public async Task Rename_saves_in_place_and_the_heading_follows()
    {
        var id = Seed("Strawberry Drink Mix");
        var cut = RenderDetail(id);

        cut.Find("button[aria-label^='Rename']").Click();
        cut.Find(".rename-input").Input("Drink Mix");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Equal("Drink Mix", cut.Find("h1").TextContent.Trim()));
        Assert.Equal("Drink Mix", (await LoadAsync(id)).Name);
    }

    [Fact]
    public void Recipes_that_use_this_lists_a_recipe_grounded_by_a_PUNCTUATION_variant()
    {
        // ⚠️ Finding P. RecipeIngredient.MatchedProduct is a name captured at save time; a punctuation
        // variant of this product's name is the same product to every write-side guard, so "recipes that use
        // this" matches by IDENTITY, not raw equality — else a recipe grounded to "Home Canned Sauce" would
        // not show on the "Home-Canned Sauce" page (and a raw string.Equals did exactly that).
        var id = Seed("Home-Canned Sauce");
        using (var db = Db.CreateDbContext())
        {
            db.Recipes.Add(new Recipe
            {
                Name = "Pasta Night",
                Ingredients = [new RecipeIngredient { Name = "Sauce", IsMain = true, MatchedProduct = "Home Canned Sauce" }],
            });
            db.SaveChanges();
        }

        var cut = RenderDetail(id);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Recipes that use this", cut.Markup);
            Assert.Contains("Pasta Night", cut.Markup);
        });
    }

    [Fact]
    public async Task Enter_saves_a_rename_and_Escape_cancels_one()
    {
        var id = Seed("Old Name");
        var cut = RenderDetail(id);

        cut.Find("button[aria-label^='Rename']").Click();
        cut.Find(".rename-input").Input("Escaped Name");
        cut.Find(".rename-input").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        // Escape abandons: the editor closes, nothing was written.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".rename-input")));
        Assert.Equal("Old Name", (await LoadAsync(id)).Name);

        cut.Find("button[aria-label^='Rename']").Click();
        cut.Find(".rename-input").Input("Entered Name");
        cut.Find(".rename-input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() => Assert.Equal("Entered Name", cut.Find("h1").TextContent.Trim()));
        Assert.Equal("Entered Name", (await LoadAsync(id)).Name);
    }

    [Fact]
    public async Task A_rename_collision_shows_the_services_refusal_and_changes_nothing()
    {
        Seed("Taken Name");
        var id = Seed("My Product");
        var cut = RenderDetail(id);

        cut.Find("button[aria-label^='Rename']").Click();
        cut.Find(".rename-input").Input("taken name"); // case differences are still the same product
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEqual("", cut.Find("p.error").TextContent.Trim()); // the service's message renders
            // The editor stays open holding the refused value — closing it would discard the
            // user's typing along with the explanation of why it didn't take.
            Assert.Equal("taken name", cut.Find(".rename-input").GetAttribute("value"));
        });
        Assert.Equal("My Product", (await LoadAsync(id)).Name);
    }

    // ------------------------------------------------------------------------------------- merge

    private (int SourceId, int KinId, int StrangerId) SeedMergeCatalog()
    {
        using var db = Db.CreateDbContext();
        var source = new Product
        {
            Name = "Strawberry Drink Mix",
            Category = Category.Pantry,
            Tags = [new ProductTag { Value = "drink mix" }],
        };
        var kin = new Product
        {
            Name = "Drink Mix",
            Category = Category.Pantry,
            Tags = [new ProductTag { Value = "drink mix" }],
        };
        var stranger = new Product
        {
            Name = "Apple Juice",
            Category = Category.Pantry,
            Tags = [new ProductTag { Value = "juice" }],
        };
        db.Products.AddRange(source, kin, stranger);
        db.SaveChanges();
        return (source.Id, kin.Id, stranger.Id);
    }

    private static List<string> SelectOptions(IRenderedComponent<ProductDetail> cut) =>
        [.. cut.FindAll(".merge-panel select option").Skip(1).Select(o => o.TextContent.Trim())];

    [Fact]
    public void Opening_the_merge_prefilters_candidates_to_the_products_own_tag()
    {
        var (sourceId, _, _) = SeedMergeCatalog();
        var cut = RenderDetail(sourceId);

        cut.Find("button[aria-label^='Merge']").Click();

        // Merging repairs a split ITEM, so its kin almost always share a tag — the panel opens
        // narrowed to it, with the stranger hidden and one tap on the active chip clearing back.
        cut.WaitForAssertion(() => Assert.Equal(["Drink Mix"], SelectOptions(cut)));
        var active = cut.Find(".tag-cloud-chip.active");
        Assert.StartsWith("drink mix", active.TextContent.Trim());

        cut.Find(".tag-clear").Click();
        cut.WaitForAssertion(() => Assert.Equal(["Apple Juice", "Drink Mix"], SelectOptions(cut)));
    }

    [Fact]
    public void Picking_a_target_prefills_the_variety_from_the_name_difference()
    {
        var (sourceId, kinId, _) = SeedMergeCatalog();
        var cut = RenderDetail(sourceId);
        cut.Find("button[aria-label^='Merge']").Click();
        cut.WaitForState(() => cut.FindAll(".merge-panel").Count > 0);

        cut.Find(".merge-panel select").Change(kinId.ToString());

        // "Strawberry Drink Mix" minus "Drink Mix" leaves the flavor the moved purchases lose —
        // pre-filled, never forced.
        cut.WaitForAssertion(() => Assert.Equal("Strawberry",
            cut.Find("input[aria-label='Variety label for the moved purchases']").GetAttribute("value")));
    }

    [Fact]
    public void A_target_hidden_by_the_tag_filter_cannot_stay_silently_selected()
    {
        var (sourceId, kinId, _) = SeedMergeCatalog();
        var cut = RenderDetail(sourceId);
        cut.Find("button[aria-label^='Merge']").Click();
        cut.WaitForState(() => cut.FindAll(".merge-panel").Count > 0);
        cut.Find(".merge-panel select").Change(kinId.ToString());

        // Switch the cloud to a tag the chosen target lacks: the user can no longer see what the
        // Merge button would hit, so the selection resets and the button re-disables.
        cut.FindAll(".tag-cloud-chip").Single(c => c.TextContent.Contains("juice")).Click();

        cut.WaitForAssertion(() =>
        {
            var merge = cut.FindAll(".merge-panel button").Single(b => b.TextContent.Trim() == "Merge");
            Assert.True(merge.HasAttribute("disabled"));
        });
    }

    [Fact]
    public async Task Merge_navigates_to_the_surviving_product()
    {
        var (sourceId, kinId, _) = SeedMergeCatalog();
        var cut = RenderDetail(sourceId);
        cut.Find("button[aria-label^='Merge']").Click();
        cut.WaitForState(() => cut.FindAll(".merge-panel").Count > 0);

        var mergeBefore = cut.FindAll(".merge-panel button").Single(b => b.TextContent.Trim() == "Merge");
        Assert.True(mergeBefore.HasAttribute("disabled")); // no target picked yet

        cut.Find(".merge-panel select").Change(kinId.ToString());
        cut.FindAll(".merge-panel button").Single(b => b.TextContent.Trim() == "Merge").Click();

        // The page's subject no longer exists — staying put would render a deleted product.
        var nav = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith($"/product/{kinId}", nav.Uri));

        await using var raw = Db.CreateUnscopedContext();
        Assert.Null(await raw.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == sourceId));
    }

    [Fact]
    public void Merge_stashes_the_inline_undo_for_the_target_page()
    {
        var (sourceId, kinId, _) = SeedMergeCatalog();
        var cut = RenderDetail(sourceId);
        cut.Find("button[aria-label^='Merge']").Click();
        cut.WaitForState(() => cut.FindAll(".merge-panel").Count > 0);
        cut.Find(".merge-panel select").Change(kinId.ToString());
        cut.FindAll(".merge-panel button").Single(b => b.TextContent.Trim() == "Merge").Click();

        var nav = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith($"/product/{kinId}", nav.Uri));

        // The inline ↩ Undo was stashed for the target page to pick up, naming the merged-away source and
        // carrying the undo entry id.
        var pending = MergeNotice.TakeFor(kinId);
        Assert.NotNull(pending);
        Assert.Equal("Strawberry Drink Mix", pending!.SourceName);
        Assert.True(pending.EntryId > 0);
    }

    [Fact]
    public async Task The_target_page_shows_the_inline_undo_and_it_reverses_the_merge()
    {
        var (sourceId, kinId, _) = SeedMergeCatalog();
        // The real merge, then stash the notice exactly as SaveMerge does (the source is now deleted).
        var result = await Services.GetRequiredService<ProductMergeService>().MergeAsync(sourceId, kinId);
        Assert.True(result.Ok);
        MergeNotice.Set(result.UndoEntryId, "Strawberry Drink Mix", kinId);

        var cut = RenderDetail(kinId); // land on the target, as the post-merge navigation would

        // The notice names the merged-away source and offers ↩ Undo.
        cut.WaitForAssertion(() =>
        {
            var callout = cut.Find(".callout");
            Assert.Contains("Merged", callout.TextContent);
            Assert.Contains("Strawberry Drink Mix", callout.TextContent);
        });

        cut.Find(".callout button").Click(); // ↩ Undo

        // The success confirmation replaces the button (NOT the failure branch, whose copy also contains
        // "undone" — the specific "Merge undone" + the vanished button are what tell them apart).
        cut.WaitForAssertion(() =>
        {
            var callout = cut.Find(".callout");
            Assert.Contains("Merge undone", callout.TextContent);
            Assert.Empty(callout.QuerySelectorAll("button"));
        });
        await using var raw = Db.CreateUnscopedContext();
        Assert.NotNull(await raw.Products.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Name == "Strawberry Drink Mix"));
    }

    [Fact]
    public async Task The_inline_undo_clears_on_a_switch_to_a_different_product()
    {
        var (sourceId, kinId, strangerId) = SeedMergeCatalog();
        var result = await Services.GetRequiredService<ProductMergeService>().MergeAsync(sourceId, kinId);
        MergeNotice.Set(result.UndoEntryId, "Strawberry Drink Mix", kinId);

        var cut = RenderDetail(kinId);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".callout")));

        // The notice belongs to the target it landed on; navigating to a different product clears it.
        cut.Render(ps => ps.Add(p => p.Id, strangerId));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".callout")));
    }

    // ------------------------------------------------------------------- purchase-quantity edit

    [Fact]
    public async Task Correcting_a_purchase_moves_the_count_by_the_difference_not_to_the_value()
    {
        var id = Seed("Canned Beans", p =>
        {
            p.TrackQuantity = true;
            p.QuantityOnHand = 5m;
            p.QuantityCountedAt = CountedClock;
            p.Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-15), Quantity = 1m },
            ];
        });
        var cut = RenderDetail(id);

        cut.Find("button[aria-label^='Correct the quantity for the " + $"{Today.AddDays(-15):MMM d}" + "']").Click();
        cut.Find("input[aria-label^='Corrected quantity']").Change("3");
        cut.FindAll("table button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Contains("3", cut.Find("tbody tr td:nth-child(3)").TextContent));

        var product = await LoadAsync(id);
        Assert.Equal(3m, product.Purchases.Single(x => x.PurchasedAt == Today.AddDays(-15)).Quantity);
        // §13.6: the shelf moves by the DIFFERENCE (+2) — and correcting the receipt's record is
        // not a look at the shelf, so the attestation clock stays put.
        Assert.Equal(7m, product.QuantityOnHand);
        Assert.Equal(CountedClock, product.QuantityCountedAt);
    }

    [Fact]
    public async Task A_non_positive_correction_is_refused_toward_receipt_removal()
    {
        var id = Seed("Canned Beans", p =>
            p.Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-15), Quantity = 2m }]);
        var cut = RenderDetail(id);

        cut.Find("button[aria-label^='Correct the quantity']").Click();
        cut.Find("input[aria-label^='Corrected quantity']").Change("0");
        cut.FindAll("table button").Single(b => b.TextContent.Trim() == "Save").Click();

        // Zero would mean "this purchase never happened", which is the receipt-removal flow's job —
        // a quantity edit refusing is what keeps that deletion deliberate.
        cut.WaitForAssertion(() => Assert.Equal(
            "A purchase has to be more than zero — to remove it entirely, remove its receipt.",
            cut.Find("p.error").TextContent.Trim()));
        Assert.Equal(2m, (await LoadAsync(id)).Purchases.Single().Quantity);
    }

    [Fact]
    public async Task The_correction_failure_advice_splits_on_which_context_died()
    {
        var id = Seed("Canned Beans", p =>
            p.Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-15), Quantity = 1m }]);
        var cut = RenderDetail(id);
        cut.Find("button[aria-label^='Correct the quantity']").Click();
        cut.Find("input[aria-label^='Corrected quantity']").Change("4");

        Factory.FailAfter = 0; // the store's write dies — nothing landed
        cut.FindAll("table button").Single(b => b.TextContent.Trim() == "Save").Click();
        cut.WaitForAssertion(() => Assert.Equal(
            "That didn't save. Reload the page and try again.",
            cut.Find("p.error").TextContent.Trim()));
        Factory.FailAfter = null;
        Assert.Equal(1m, (await LoadAsync(id)).Purchases.Single().Quantity);

        Factory.FailAfter = 1; // the write lands; the reload dies
        cut.FindAll("table button").Single(b => b.TextContent.Trim() == "Save").Click();
        cut.WaitForAssertion(() => Assert.Equal(
            "Saved — but the page couldn't refresh. Reload to see the updated history.",
            cut.Find("p.error").TextContent.Trim()));
        Factory.FailAfter = null;
        Assert.Equal(4m, (await LoadAsync(id)).Purchases.Single().Quantity);
    }

    [Fact]
    public async Task Cancel_closes_the_editor_without_writing()
    {
        var id = Seed("Canned Beans", p =>
            p.Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-15), Quantity = 2m }]);
        var cut = RenderDetail(id);

        cut.Find("button[aria-label^='Correct the quantity']").Click();
        cut.Find("input[aria-label^='Corrected quantity']").Change("9");
        cut.FindAll("table button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("input[aria-label^='Corrected quantity']")));
        Assert.Equal(2m, (await LoadAsync(id)).Purchases.Single().Quantity);
    }

    // ---------------------------------------------------------------------------- purchase brand

    [Fact]
    public async Task Correcting_a_brand_saves_and_shows()
    {
        var id = Seed("Ground Coffee", p =>
            p.Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-15), Quantity = 1m, Brand = "Genneric" }]);
        var cut = RenderDetail(id);

        cut.Find("button[aria-label^='Correct the brand for the " + $"{Today.AddDays(-15):MMM d}" + "']").Click();
        cut.Find("input[aria-label^='Corrected brand']").Change("Folgers");
        cut.FindAll("table button").Single(b => b.TextContent.Trim() == "Save").Click();

        // Brand is the 6th column: Date · Since previous · Qty · Size · Variety · Brand · Unit price.
        cut.WaitForAssertion(() => Assert.Contains("Folgers", cut.Find("tbody tr td:nth-child(6)").TextContent));
        Assert.Equal("Folgers", (await LoadAsync(id)).Purchases.Single().Brand);
    }

    [Fact]
    public async Task Clearing_a_brand_folds_it_to_unbranded()
    {
        var id = Seed("Ground Coffee", p =>
            p.Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-15), Quantity = 1m, Brand = "Folgers" }]);
        var cut = RenderDetail(id);

        cut.Find("button[aria-label^='Correct the brand']").Click();
        cut.Find("input[aria-label^='Corrected brand']").Change("");
        cut.FindAll("table button").Single(b => b.TextContent.Trim() == "Save").Click();

        // Editor closes on a successful save; the stored brand is null, not an empty string.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("input[aria-label^='Corrected brand']")));
        Assert.Null((await LoadAsync(id)).Purchases.Single().Brand);
    }

    [Fact]
    public void The_brand_and_quantity_editors_are_mutually_exclusive()
    {
        var id = Seed("Ground Coffee", p =>
            p.Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-15), Quantity = 1m, Brand = "Folgers" }]);
        var cut = RenderDetail(id);

        // Open the quantity editor, then the brand editor: a row must never be in both edit modes, so
        // EditBrand clearing editingPurchaseId (and vice versa) is load-bearing, not cosmetic.
        cut.Find("button[aria-label^='Correct the quantity']").Click();
        Assert.Single(cut.FindAll("input[aria-label^='Corrected quantity']"));

        cut.Find("button[aria-label^='Correct the brand']").Click();
        Assert.Single(cut.FindAll("input[aria-label^='Corrected brand']"));
        Assert.Empty(cut.FindAll("input[aria-label^='Corrected quantity']")); // the quantity editor closed
    }

    // ------------------------------------------------------------------------------ substitutes

    [Fact]
    public async Task Substitutes_add_by_button_or_enter_and_dedupe_case_insensitively()
    {
        var id = Seed("Chicken Breast Tenderloins");
        var cut = RenderDetail(id);

        cut.Find("input[aria-label='Add a substitute this works as']").Input("chicken breast");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add").Click();
        cut.WaitForAssertion(() => Assert.Contains("chicken breast", cut.Find(".tag-list").TextContent));

        cut.Find("input[aria-label='Add a substitute this works as']").Input("Chicken Breast");
        cut.Find("input[aria-label='Add a substitute this works as']").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        cut.WaitForAssertion(() =>
            Assert.Equal("", cut.Find("input[aria-label='Add a substitute this works as']").GetAttribute("value") ?? ""));

        // Case variants are the same phrase — one row, not two.
        Assert.Single((await LoadAsync(id)).Substitutes);
        // Through the store, so adding a substitute is logged + undoable like a chat suggest_substitutes.
        await using var raw = Db.CreateUnscopedContext();
        Assert.NotEmpty(await raw.ActivityEntries.IgnoreQueryFilters()
            .Where(e => e.Kind == ActivityKind.SubstitutesAdded).ToListAsync());
    }

    [Fact]
    public async Task Removing_a_substitute_deletes_its_row()
    {
        var id = Seed("Chicken Breast Tenderloins", p =>
            p.Substitutes = [new ProductSubstitute { Value = "chicken breast" }]);
        var cut = RenderDetail(id);

        cut.Find("button[aria-label='Remove chicken breast']").Click();

        cut.WaitForAssertion(() => Assert.Contains("None yet", cut.Find(".tag-list").TextContent));
        Assert.Empty((await LoadAsync(id)).Substitutes);

        // Through the store, so removing a substitute is logged + undoable (symmetric with adding one).
        await using var raw = Db.CreateUnscopedContext();
        var entry = await raw.ActivityEntries.IgnoreQueryFilters()
            .SingleAsync(e => e.Kind == ActivityKind.SubstituteRemoved);
        Assert.Equal("Removed \"chicken breast\" from Chicken Breast Tenderloins's substitutes", entry.Summary);
    }

    [Fact]
    public async Task Suggest_adds_only_phrases_not_already_curated()
    {
        var id = Seed("Chicken Breast Tenderloins", p =>
            p.Substitutes = [new ProductSubstitute { Value = "chicken breast" }]);
        SubstituteAdvisor.Substitutes = ["chicken breast", "chicken cutlet"];
        var cut = RenderDetail(id);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "✨ Suggest").Click();

        cut.WaitForAssertion(() => Assert.Contains("chicken cutlet", cut.Find(".tag-list").TextContent));
        // The advisor's repeat of an existing phrase must not double it.
        var substitutes = (await LoadAsync(id)).Substitutes.Select(s => s.Value).OrderBy(v => v).ToList();
        Assert.Equal(["chicken breast", "chicken cutlet"], substitutes);
    }

    // -------------------------------------------------------------------------------- price chart

    /// <summary>Seeds ONE receipt carrying several lines for the same product+size, with a purchase
    /// event per line — the "listed multiple times" shape (a pre-quantity-fix duplicate, or two produce
    /// weigh-ins). Exactly the Dentastix / Dog Pads data.</summary>
    private int SeedOneReceiptManyLines(string name, DateOnly date, string size, params decimal[] linePrices)
    {
        using var db = Db.CreateDbContext();
        var product = new Product { Name = name, Category = Category.PetCare };
        db.Products.Add(product);
        db.SaveChanges();
        var receipt = new Receipt
        {
            Merchant = "Store", PurchasedAt = date, Status = ReceiptStatus.Confirmed, ImagePath = "n/a",
            Lines = [.. linePrices.Select(price => new ReceiptLine
            {
                RawText = name, NormalizedName = name, Quantity = 1m, UnitPrice = price, Size = size, ProductId = product.Id,
            })],
        };
        db.Receipts.Add(receipt);
        db.SaveChanges();
        foreach (var _ in linePrices)
            db.PurchaseEvents.Add(new PurchaseEvent { ProductId = product.Id, PurchasedAt = date, Quantity = 1m, ReceiptId = receipt.Id, Size = size });
        db.SaveChanges();
        return product.Id;
    }

    [Fact]
    public void A_receipt_that_lists_this_item_twice_is_one_trip_so_there_is_no_price_trend_to_chart()
    {
        // The other half of the Dentastix bug: the product page charted "2 price points" (the receipt
        // averaged, then plotted per purchase event) while Trends showed a phantom ▲33%. Both now read
        // the SAME raw receipt lines through PriceSeries.Dominant — one receipt's duplicate lines are ONE
        // trip — so there is nothing to chart here, and no trend to disagree with the screen beside it.
        var id = SeedOneReceiptManyLines("Dog Treats", Today.AddDays(-3), "16 oz", 36.19m, 48.26m);
        var cut = RenderDetail(id);

        // One trip → one point → the Price history chart (which needs ≥2) never renders, and the page
        // never claims "2 price points". Before the fix it plotted two.
        Assert.DoesNotContain("Price history", cut.Markup);
        Assert.DoesNotContain("price points", cut.Markup);
    }
}
