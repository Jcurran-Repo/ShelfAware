using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Cookbook — a preview shelf over saved recipes with a stable detail panel below, plus read-aloud,
/// print, tags, photos, and a product filter. The rules under test: recipes page alphabetically and the
/// arrow keys / clicking a preview walk them with a "which of how many" announcement and an aria-current
/// marker on the centred preview; the detail panel renders the centred recipe (and is the ONE home of the
/// fixed-id photo input + tag datalist, however big the deck); the "Ready to make"/"Missing items" chip and
/// the ✓/🛒 marks agree with the Recipes page (same PantryOnHand + IngredientMatcher); the ?uses filter
/// scopes the deck to recipes grounded to that product (Recipe.Uses — the one shared definition) and the
/// dropdown navigates there; the two print buttons choose their print-only surface and fire window.print;
/// and the products list prints every ingredient with its amount, ticking the ones already on hand.
///
/// The drag / swipe / scroll-snap itself is browser behaviour (live-verified) — bUnit runs no real JS, so
/// these tests assert the Blazor side: N previews render, the detail panel + live region track the centre,
/// the arrow keys and a preview click move it, and filters still scope + reset.
/// </summary>
public class CookbookPageTests : PageTestContext
{
    private readonly FakeRecipeTagAdvisor _tagAdvisor = new();
    private readonly StubPhotoLoader _photoLoader = new();
    private readonly string _imageDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-cookbook-test", Guid.NewGuid().ToString("N"));

    protected override void RegisterAdditionalServices()
    {
        // The cookbook injects RecipeTagService + the photo seam. Register them over the real test store
        // with scriptable fakes (the AI + browser seams) and a throwaway on-disk image store. Base members
        // only here — this runs before the derived ctor body.
        Services.AddSingleton<IRecipeTagAdvisor>(_tagAdvisor);
        Services.AddSingleton(new RecipeTagService(Factory, _tagAdvisor, NullLogger<RecipeTagService>.Instance));
        Services.AddSingleton<IShelfPhotoLoader>(_photoLoader);
        Services.AddSingleton(new RecipeImageStorage(
            new AppPaths(_imageDir, Path.Combine(_imageDir, "receipts")),
            new FakeCurrentHousehold("hh-test"),
            NullLogger<RecipeImageStorage>.Instance));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(_imageDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    // A product the household has on hand (one recent purchase → not overdue → in stock).
    private int SeedProduct(string name, Category category = Category.Meat)
    {
        using var db = Db.CreateDbContext();
        var product = new Product
        {
            Name = name,
            Category = category,
            Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1 }],
        };
        db.Products.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    private int SeedRecipe(
        string name,
        (string Name, bool IsMain, string? Matched, string? Qty)[] ingredients,
        params string[] steps)
    {
        using var db = Db.CreateDbContext();
        var recipe = new Recipe
        {
            Name = name,
            SavedAt = DateTimeOffset.Now,
            Ingredients = [.. ingredients.Select(i => new RecipeIngredient
            {
                Name = i.Name, IsMain = i.IsMain, MatchedProduct = i.Matched, Quantity = i.Qty,
            })],
            Steps = [.. steps.Select((t, idx) => new RecipeStep { Order = idx + 1, Text = t })],
        };
        db.Recipes.Add(recipe);
        db.SaveChanges();
        return recipe.Id;
    }

    private IRenderedComponent<Cookbook> RenderCookbook()
    {
        var cut = Render<Cookbook>();
        cut.WaitForState(() =>
            cut.FindAll(".cookbook-shelf").Count > 0
            || cut.Markup.Contains("cookbook is empty")
            || cut.Markup.Contains("No saved recipes use")
            || cut.Markup.Contains("No recipes tagged"));
        return cut;
    }

    private BunitNavigationManager Nav => (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    // The previews in deck (alphabetical) order — one <button> per recipe in `filtered`.
    private static IReadOnlyList<AngleSharp.Dom.IElement> Previews(IRenderedComponent<Cookbook> cut) =>
        cut.FindAll(".cookbook-preview");

    // Page the shelf with an arrow key (the shelf is focusable and owns the keydown handler).
    private static void PressKey(IRenderedComponent<Cookbook> cut, string key) =>
        cut.Find(".cookbook-shelf").KeyDown(new KeyboardEventArgs { Key = key });

    // ------------------------------------------------------------------ empty / browse / order

    [Fact]
    public void An_empty_cookbook_points_at_Recipes_and_shows_no_shelf()
    {
        var cut = RenderCookbook();

        Assert.Contains("cookbook is empty", cut.Markup);
        Assert.Empty(cut.FindAll(".cookbook-shelf"));
    }

    [Fact]
    public void The_shelf_renders_one_preview_per_recipe_alphabetically_the_first_centred()
    {
        SeedRecipe("Zucchini Bake", [("Zucchini", true, null, null)], "Bake it.");
        SeedRecipe("Apple Crisp", [("Apples", true, null, null)], "Bake it.");

        var cut = RenderCookbook();

        // One preview per recipe, in alphabetical (deck) order — not save order.
        var previews = Previews(cut);
        Assert.Equal(2, previews.Count);
        Assert.Contains("Apple Crisp", previews[0].TextContent);
        Assert.Contains("Zucchini Bake", previews[1].TextContent);

        // The first is centred: the live region, the detail panel, and aria-current all point at it.
        Assert.Contains("Apple Crisp", cut.Find(".cookbook-position").TextContent);
        Assert.Contains("1 of 2", Collapsed(cut.Find(".cookbook-position")));
        Assert.Equal("Apple Crisp", cut.Find(".cookbook-detail h2").TextContent);
        Assert.Equal("true", previews[0].GetAttribute("aria-current"));
        Assert.False(previews[1].HasAttribute("aria-current"));
    }

    [Fact]
    public void Arrow_keys_page_the_shelf_and_stop_at_the_ends()
    {
        SeedRecipe("Apple Crisp", [("Apples", true, null, null)], "Bake it.");
        SeedRecipe("Banana Bread", [("Bananas", true, null, null)], "Bake it.");

        var cut = RenderCookbook();

        // Left at the start is a no-op — still on the first recipe.
        PressKey(cut, "ArrowLeft");
        Assert.Contains("Apple Crisp", cut.Find(".cookbook-position").TextContent);

        PressKey(cut, "ArrowRight");
        Assert.Contains("Banana Bread", cut.Find(".cookbook-position").TextContent);
        Assert.Equal("Banana Bread", cut.Find(".cookbook-detail h2").TextContent);
        Assert.Equal("true", Previews(cut)[1].GetAttribute("aria-current"));

        // Right at the end is a no-op — still on the last recipe.
        PressKey(cut, "ArrowRight");
        Assert.Contains("Banana Bread", cut.Find(".cookbook-position").TextContent);

        PressKey(cut, "ArrowLeft");
        Assert.Contains("Apple Crisp", cut.Find(".cookbook-position").TextContent);
    }

    [Fact]
    public void Home_and_End_jump_to_the_ends_of_the_deck()
    {
        SeedRecipe("Apple Crisp", [("Apples", true, null, null)], "Bake.");
        SeedRecipe("Banana Bread", [("Bananas", true, null, null)], "Bake.");
        SeedRecipe("Cherry Pie", [("Cherries", true, null, null)], "Bake.");

        var cut = RenderCookbook();

        PressKey(cut, "End");
        Assert.Contains("Cherry Pie", cut.Find(".cookbook-position").TextContent);
        Assert.Contains("3 of 3", Collapsed(cut.Find(".cookbook-position")));

        PressKey(cut, "Home");
        Assert.Contains("Apple Crisp", cut.Find(".cookbook-position").TextContent);
        Assert.Contains("1 of 3", Collapsed(cut.Find(".cookbook-position")));
    }

    [Fact]
    public void Clicking_a_preview_centres_it()
    {
        SeedRecipe("Apple Crisp", [("Apples", true, null, null)], "Bake.");
        SeedRecipe("Banana Bread", [("Bananas", true, null, null)], "Bake.");
        SeedRecipe("Cherry Pie", [("Cherries", true, null, null)], "Bake.");

        var cut = RenderCookbook();
        Previews(cut)[2].Click(); // click the third preview

        Assert.Contains("Cherry Pie", cut.Find(".cookbook-position").TextContent);
        Assert.Equal("Cherry Pie", cut.Find(".cookbook-detail h2").TextContent);
        Assert.Equal("true", Previews(cut)[2].GetAttribute("aria-current"));
        Assert.False(Previews(cut)[0].HasAttribute("aria-current"));
    }

    [Fact]
    public void One_photo_input_and_one_datalist_exist_however_big_the_deck()
    {
        // The whole reason for the preview-shelf design: the detail panel is the ONE home of the fixed-id
        // photo <InputFile id="cookbook-photo"> and <datalist id="recipe-tag-vocab">, so a deck of many
        // recipes doesn't duplicate those ids (invalid HTML, three tag boxes fighting for focus).
        SeedRecipe("Apple Crisp", [("Apples", true, null, null)], "Bake.");
        SeedRecipe("Banana Bread", [("Bananas", true, null, null)], "Bake.");
        SeedRecipe("Cherry Pie", [("Cherries", true, null, null)], "Bake.");

        var cut = RenderCookbook();

        Assert.Equal(3, Previews(cut).Count);
        Assert.Single(cut.FindAll("input#cookbook-photo"));
        Assert.Single(cut.FindAll("datalist#recipe-tag-vocab"));
    }

    // OnCentered is the JS→.NET half of the sync — the drag/swipe module reports the settled centre here.
    // bUnit runs no real JS, so we call it directly (it's the only new sync logic with no other test).

    [Fact]
    public async Task OnCentered_moves_the_centre_the_way_a_drag_reports_it()
    {
        SeedRecipe("Apple Crisp", [("Apples", true, null, null)], "Bake.");
        SeedRecipe("Banana Bread", [("Bananas", true, null, null)], "Bake.");

        var cut = RenderCookbook();
        await cut.InvokeAsync(() => cut.Instance.OnCentered(1)); // the shelf settled on the 2nd preview

        Assert.Contains("Banana Bread", cut.Find(".cookbook-position").TextContent);
        Assert.Equal("Banana Bread", cut.Find(".cookbook-detail h2").TextContent);
        Assert.Equal("true", Previews(cut)[1].GetAttribute("aria-current"));
    }

    [Fact]
    public async Task OnCentered_for_the_current_index_is_a_no_op()
    {
        SeedRecipe("Apple Crisp", [("Apples", true, null, null)], "Bake.");
        SeedRecipe("Banana Bread", [("Bananas", true, null, null)], "Bake.");

        var cut = RenderCookbook();
        cut.Find(".cookbook-tag-add input").Input("Draft"); // unsaved text in the add box

        await cut.InvokeAsync(() => cut.Instance.OnCentered(0)); // already centred on index 0

        // A no-op: it must NOT re-run ResetTransient (which would clear the add box) or move the centre.
        Assert.Equal("Draft", cut.Find(".cookbook-tag-add input").GetAttribute("value"));
        Assert.Contains("Apple Crisp", cut.Find(".cookbook-position").TextContent);
    }

    [Fact]
    public async Task OnCentered_clamps_a_stale_out_of_range_index()
    {
        SeedRecipe("Apple Crisp", [("Apples", true, null, null)], "Bake.");
        SeedRecipe("Banana Bread", [("Bananas", true, null, null)], "Bake.");

        var cut = RenderCookbook();
        // The deck can shrink (a filter) between the JS read and this call — a stale index must not throw.
        await cut.InvokeAsync(() => cut.Instance.OnCentered(99));

        Assert.Contains("2 of 2", Collapsed(cut.Find(".cookbook-position"))); // clamped to the last card
        Assert.Equal("Banana Bread", cut.Find(".cookbook-detail h2").TextContent);
    }

    // ------------------------------------------------------------------ makeability marks

    [Fact]
    public void A_recipe_you_can_make_reads_ready_and_ticks_its_on_hand_main()
    {
        SeedProduct("Chicken Breast");
        SeedRecipe("Chicken Dinner", [("Chicken Breast", true, "Chicken Breast", "2 breasts")], "Cook it.");

        var cut = RenderCookbook();

        Assert.Contains("Ready to make", cut.Find(".cookbook-detail .chip-stocked").TextContent);
        Assert.Contains("✓", cut.Find(".ingredient-list li.have").TextContent);
        Assert.Empty(cut.FindAll(".ingredient-list li.grab"));
    }

    [Fact]
    public void A_recipe_missing_a_main_reads_missing_items()
    {
        SeedRecipe("Tofu Scramble", [("Tofu", true, "Tofu", null)], "Cook it.");

        var cut = RenderCookbook();

        Assert.Contains("Missing items", cut.Find(".cookbook-detail .chip-unknown").TextContent);
        Assert.Contains("🛒", cut.Find(".ingredient-list li.grab").TextContent);
        Assert.Empty(cut.FindAll(".ingredient-list li.have"));
    }

    [Fact]
    public void A_recipe_covered_only_by_a_stand_in_reads_makeable_with_a_swap()
    {
        // You own Chuck Roast and marked it "also works as" steak, but not steak itself. The Cookbook badge
        // must match the Recipes page via the shared MakeabilityFormat — "Makeable with a swap" (amber), not
        // "Ready to make" — and the covered ingredient flags the stand-in.
        var chuckId = SeedProduct("Chuck Roast");
        using (var db = Db.CreateDbContext())
        {
            db.ProductSubstitutes.Add(new ProductSubstitute { ProductId = chuckId, Value = "steak" });
            db.SaveChanges();
        }
        SeedRecipe("Steak Dinner", [("steak", true, null, null)], "Sear it.");

        var cut = RenderCookbook();

        Assert.Contains("Makeable with a swap", cut.Find(".cookbook-detail .chip-duesoon").TextContent);
        Assert.Empty(cut.FindAll(".cookbook-detail .chip-stocked"));       // not "Ready to make"
        Assert.Single(cut.FindAll(".ingredient-list li.have .swap-note")); // covered (✓) but flagged a stand-in
    }

    // ------------------------------------------------------------------ product filter

    [Fact]
    public void Filtering_by_a_product_scopes_the_deck_to_recipes_that_use_it()
    {
        var chickenId = SeedProduct("Chicken Breast");
        SeedRecipe("Chicken Dinner", [("Chicken Breast", true, "Chicken Breast", null)], "Cook it.");
        SeedRecipe("Veggie Stir Fry", [("Broccoli", true, "Broccoli", null)], "Cook it.");

        Nav.NavigateTo($"/cookbook?uses={chickenId}");
        var cut = RenderCookbook();

        Assert.Contains("Showing recipes that use Chicken Breast", cut.Find(".filter-banner").TextContent);
        Assert.Contains("1 of 1", Collapsed(cut.Find(".cookbook-position")));
        Assert.Single(Previews(cut)); // the deck is scoped to the one matching recipe
        Assert.Equal("Chicken Dinner", cut.Find(".cookbook-detail h2").TextContent);
        Assert.DoesNotContain("Veggie Stir Fry", cut.Markup);
    }

    [Fact]
    public void A_filter_that_matches_no_recipe_says_so()
    {
        var oatsId = SeedProduct("Rolled Oats", Category.Pantry);
        // A recipe exists, but it doesn't use the oats — so the oats filter matches nothing.
        SeedRecipe("Chicken Dinner", [("Chicken Breast", true, "Chicken Breast", null)], "Cook it.");

        Nav.NavigateTo($"/cookbook?uses={oatsId}");
        var cut = RenderCookbook();

        Assert.Contains("No saved recipes use Rolled Oats", cut.Markup);
        Assert.Empty(cut.FindAll(".cookbook-shelf"));
    }

    [Fact]
    public void Typing_a_products_name_in_the_filter_navigates_to_that_filter()
    {
        var chickenId = SeedProduct("Chicken Breast");
        SeedRecipe("Chicken Dinner", [("Chicken Breast", true, "Chicken Breast", null)], "Cook it.");

        var cut = RenderCookbook();
        // A datalist-backed type-and-guess box: committing an exact product name resolves it to the ?uses id.
        cut.Find(".cookbook-product-input").Change("Chicken Breast");

        Assert.EndsWith($"/cookbook?uses={chickenId}", Nav.Uri);
    }

    [Fact]
    public void A_half_typed_product_name_resolves_to_the_unique_match()
    {
        var chickenId = SeedProduct("Chicken Breast");
        SeedRecipe("Chicken Dinner", [("Chicken Breast", true, "Chicken Breast", null)], "Cook it.");

        var cut = RenderCookbook();
        // Typing a partial and committing (Enter without picking a suggestion) still lands, when it's unambiguous.
        cut.Find(".cookbook-product-input").Change("chick");

        Assert.EndsWith($"/cookbook?uses={chickenId}", Nav.Uri);
    }

    [Fact]
    public void An_exact_name_wins_even_when_it_is_a_substring_of_another_product()
    {
        var chickenId = SeedProduct("Chicken");
        SeedProduct("Chicken Breast");
        SeedRecipe("Roast Chicken", [("Chicken", true, "Chicken", null)], "Cook it.");
        SeedRecipe("Breast Dinner", [("Chicken Breast", true, "Chicken Breast", null)], "Cook it.");

        var cut = RenderCookbook();
        // "Chicken" is a substring of "Chicken Breast", so a partial match is ambiguous — but the EXACT
        // name must still resolve to the "Chicken" product (exact is checked before partial).
        cut.Find(".cookbook-product-input").Change("Chicken");

        Assert.EndsWith($"/cookbook?uses={chickenId}", Nav.Uri);
    }

    [Fact]
    public void An_ambiguous_partial_matches_nothing_and_changes_no_filter()
    {
        SeedProduct("Chicken Breast");
        SeedProduct("Chicken Thighs");
        SeedRecipe("Roast Breast", [("Chicken Breast", true, "Chicken Breast", null)], "Cook it.");
        SeedRecipe("Braised Thighs", [("Chicken Thighs", true, "Chicken Thighs", null)], "Cook it.");

        var cut = RenderCookbook();
        var before = Nav.History.Count;
        cut.Find(".cookbook-product-input").Change("chicken"); // matches BOTH offered products — ambiguous

        Assert.Equal(before, Nav.History.Count); // no product chosen, so no navigation
    }

    [Fact]
    public void An_unrecognised_entry_changes_no_filter()
    {
        SeedProduct("Chicken Breast");
        SeedRecipe("Chicken Dinner", [("Chicken Breast", true, "Chicken Breast", null)], "Cook it.");

        var cut = RenderCookbook();
        var before = Nav.History.Count;
        cut.Find(".cookbook-product-input").Change("nothing like this");

        Assert.Equal(before, Nav.History.Count);
    }

    [Fact]
    public void A_committed_entry_that_matches_nothing_says_so_instead_of_silently_ignoring_it()
    {
        SeedProduct("Chicken Breast");
        SeedProduct("Chicken Thighs");
        SeedRecipe("Roast Breast", [("Chicken Breast", true, "Chicken Breast", null)], "Cook.");
        SeedRecipe("Braised Thighs", [("Chicken Thighs", true, "Chicken Thighs", null)], "Cook.");

        var cut = RenderCookbook();
        var before = Nav.History.Count;
        // "chicken" matches BOTH offered products → resolves to nothing. The old <select> couldn't leave
        // you with no response; the type-and-guess box can, so it must say why, not silently ignore it.
        cut.Find(".cookbook-product-input").Change("chicken");

        Assert.Equal(before, Nav.History.Count); // still no navigation…
        Assert.Contains("No single product matches", cut.Find(".cookbook-product-filter").TextContent); // …but explained
    }

    [Fact]
    public void The_no_match_note_does_not_linger_after_the_filter_changes_by_another_route()
    {
        var chickenId = SeedProduct("Chicken Breast");
        SeedProduct("Chicken Thighs");
        SeedRecipe("Roast Breast", [("Chicken Breast", true, "Chicken Breast", null)], "Cook.");
        SeedRecipe("Braised Thighs", [("Chicken Thighs", true, "Chicken Thighs", null)], "Cook.");

        var cut = RenderCookbook();
        cut.Find(".cookbook-product-input").Change("chicken"); // ambiguous → the note appears
        Assert.Contains("No single product matches", cut.Find(".cookbook-product-filter").TextContent);

        // A navigation that doesn't pass through the product box (a shared ?uses link, a tag chip) reloads
        // onto a real filter — the stale note must clear, not sit contradicting the box's reset text.
        Nav.NavigateTo($"/cookbook?uses={chickenId}");

        Assert.DoesNotContain("No single product matches", cut.Find(".cookbook-product-filter").TextContent);
    }

    [Fact]
    public void Clearing_the_filter_box_shows_all_recipes_again()
    {
        var chickenId = SeedProduct("Chicken Breast");
        SeedRecipe("Chicken Dinner", [("Chicken Breast", true, "Chicken Breast", null)], "Cook it.");
        SeedRecipe("Veggie Stir Fry", [("Broccoli", true, "Broccoli", null)], "Cook it.");

        Nav.NavigateTo($"/cookbook?uses={chickenId}");
        var cut = RenderCookbook();
        Assert.Single(Previews(cut)); // scoped to the one chicken recipe

        cut.Find(".cookbook-product-input").Change(""); // clear the box → back to all

        Assert.DoesNotContain("uses=", Nav.Uri);
        Assert.Equal(2, Previews(cut).Count);
    }

    // ------------------------------------------------------------------ print + audio wiring

    [Fact]
    public void Printing_the_recipe_chooses_the_recipe_surface_and_fires_window_print()
    {
        SeedRecipe("Chicken Dinner", [("Chicken Breast", true, "Chicken Breast", null)], "Cook it.");

        var cut = RenderCookbook();
        cut.Find("button[aria-label='Print Chicken Dinner']").Click();

        Assert.Contains("show-recipe", cut.Find(".cookbook-print").GetAttribute("class"));
        cut.WaitForAssertion(() => JSInterop.VerifyInvoke("window.print"));
    }

    [Fact]
    public void Printing_the_products_lists_every_ingredient_with_amounts_and_ticks_on_hand()
    {
        SeedProduct("Chicken Breast");
        SeedRecipe(
            "Chicken Dinner",
            [("Chicken Breast", true, "Chicken Breast", "2 lbs"), ("Salt", false, null, "1 tsp")],
            "Cook it.");

        var cut = RenderCookbook();
        cut.Find("button[aria-label='Print the products needed for Chicken Dinner']").Click();

        Assert.Contains("show-products", cut.Find(".cookbook-print").GetAttribute("class"));
        var products = Collapsed(cut.Find(".print-products"));
        Assert.Contains("☑ 2 lbs Chicken Breast", products); // a main, on hand
        Assert.Contains("1 tsp Salt", products);             // a seasoning, listed with its amount
        cut.WaitForAssertion(() => JSInterop.VerifyInvoke("window.print"));
    }

    [Fact]
    public void Read_it_to_me_appears_only_for_recipes_that_have_steps()
    {
        SeedRecipe("Has Steps", [("Anything", true, null, null)], "Do the thing.");
        SeedRecipe("No Steps", [("Anything", true, null, null)]); // no method to read

        var cut = RenderCookbook(); // "Has Steps" sorts first

        Assert.Single(cut.FindAll("button[aria-label='Read Has Steps aloud']"));
        Assert.Single(cut.FindAll("button[aria-label='Print Has Steps']"));

        PressKey(cut, "ArrowRight"); // → "No Steps"

        Assert.Empty(cut.FindAll("button[aria-label='Read No Steps aloud']"));
        Assert.Single(cut.FindAll("button[aria-label='Print No Steps']")); // print still offered
    }

    // ------------------------------------------------------------------ tags

    private void AddTagInDb(int recipeId, params string[] tags)
    {
        using var db = Db.CreateDbContext();
        var recipe = db.Recipes.Include(r => r.Tags).First(r => r.Id == recipeId);
        foreach (var t in tags) recipe.Tags.Add(new RecipeTag { Value = t });
        db.SaveChanges();
    }

    [Fact]
    public void The_tag_cloud_lists_each_tag_with_its_count()
    {
        AddTagInDb(SeedRecipe("Pasta Night", [("Pasta", true, null, null)], "Boil."), "Dinner", "Italian");
        AddTagInDb(SeedRecipe("Taco Tuesday", [("Beef", true, null, null)], "Cook."), "Dinner", "Mexican");

        var cut = RenderCookbook();

        var cloud = Collapsed(cut.Find(".tag-cloud"));
        Assert.Contains("Dinner 2", cloud); // shared by both recipes
        Assert.Contains("Italian 1", cloud);
        Assert.Contains("Mexican 1", cloud);
    }

    [Fact]
    public void Clicking_a_tag_navigates_to_that_tag_filter()
    {
        AddTagInDb(SeedRecipe("Pasta Night", [("Pasta", true, null, null)], "Boil."), "Italian");

        var cut = RenderCookbook();
        cut.FindAll(".tag-cloud button").Single(b => b.TextContent.Contains("Italian")).Click();

        Assert.EndsWith("/cookbook?tag=Italian", Nav.Uri);
    }

    [Fact]
    public void Filtering_by_tag_scopes_the_deck()
    {
        AddTagInDb(SeedRecipe("Pasta Night", [("Pasta", true, null, null)], "Boil."), "Italian");
        AddTagInDb(SeedRecipe("Taco Tuesday", [("Beef", true, null, null)], "Cook."), "Mexican");

        Nav.NavigateTo("/cookbook?tag=Italian");
        var cut = RenderCookbook();

        Assert.Contains("Showing recipes tagged", cut.Find(".filter-banner").TextContent);
        Assert.Single(Previews(cut));
        Assert.Equal("Pasta Night", cut.Find(".cookbook-detail h2").TextContent);
        Assert.DoesNotContain("Taco Tuesday", cut.Markup);
    }

    [Fact]
    public void Adding_a_tag_shows_the_chip_and_persists_it()
    {
        SeedRecipe("Plain Dish", [("Thing", true, null, null)], "Do it.");

        var cut = RenderCookbook();
        cut.Find(".cookbook-tag-add input").Input("Dinner"); // @bind:event=oninput
        cut.FindAll(".cookbook-tag-add button").Single(b => b.TextContent.Trim() == "Add").Click();

        cut.WaitForAssertion(() => Assert.Contains("Dinner", Collapsed(cut.Find(".cookbook-tags"))));
        using var db = Db.CreateDbContext();
        Assert.Contains("Dinner", db.RecipeTags.Select(t => t.Value).ToList());
    }

    [Fact]
    public void Removing_a_tag_deletes_it()
    {
        AddTagInDb(SeedRecipe("Plain Dish", [("Thing", true, null, null)], "Do it."), "Dinner");

        var cut = RenderCookbook();
        cut.Find("button[aria-label='Remove tag Dinner from Plain Dish']").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Dinner", Collapsed(cut.Find(".cookbook-tags"))));
        using var db = Db.CreateDbContext();
        Assert.Empty(db.RecipeTags.ToList());
    }

    [Fact]
    public void Suggest_tags_applies_the_advisors_suggestions()
    {
        _tagAdvisor.Next = ["Dinner", "Italian"];
        SeedRecipe("Mystery Dish", [("Thing", true, null, null)], "Do it.");

        var cut = RenderCookbook();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Suggest tags")).Click();

        cut.WaitForAssertion(() =>
        {
            var tags = Collapsed(cut.Find(".cookbook-tags"));
            Assert.Contains("Dinner", tags);
            Assert.Contains("Italian", tags);
            Assert.Contains("Added: Dinner, Italian", cut.Markup);
        });
    }

    [Fact]
    public void Tag_untagged_sweeps_every_recipe_without_tags()
    {
        _tagAdvisor.Next = ["Dinner"];
        SeedRecipe("Alpha", [("A", true, null, null)], "Do.");
        SeedRecipe("Beta", [("B", true, null, null)], "Do.");

        var cut = RenderCookbook();
        cut.Find(".cookbook-bulk-tag").Click();

        cut.WaitForAssertion(() => Assert.Contains("Tagged 2 of 2", cut.Markup));
        using var db = Db.CreateDbContext();
        Assert.Equal(2, db.RecipeTags.Count(t => t.Value == "Dinner"));
    }

    [Fact]
    public void A_failed_tag_write_shows_a_message_instead_of_tearing_down_the_circuit()
    {
        SeedRecipe("Plain Dish", [("Thing", true, null, null)], "Do it.");
        var cut = RenderCookbook();

        Factory.FailAfter = 0; // the AddAsync write's context dies (the render's loads already spent theirs)
        cut.Find(".cookbook-tag-add input").Input("Dinner");
        cut.FindAll(".cookbook-tag-add button").Single(b => b.TextContent.Trim() == "Add").Click();

        // Caught and surfaced as a message — the page stays alive rather than tearing down the circuit.
        cut.WaitForAssertion(() => Assert.Contains("Couldn't add that tag", cut.Markup));
    }

    // ------------------------------------------------------------------ photos

    private void SetImagePathInDb(int recipeId, string path)
    {
        using var db = Db.CreateDbContext();
        var recipe = db.Recipes.First(r => r.Id == recipeId);
        recipe.ImagePath = path;
        db.SaveChanges();
    }

    [Fact]
    public void A_recipe_with_a_photo_renders_the_image_with_a_cache_buster()
    {
        var id = SeedRecipe("Photo Dish", [("Thing", true, null, null)], "Do.");
        SetImagePathInDb(id, "recipe-images/hh/abc123.jpg");

        var cut = RenderCookbook();

        var src = cut.Find("img.cookbook-photo").GetAttribute("src")!;
        Assert.Contains($"/api/recipe-image/{id}", src);
        Assert.Contains("abc123.jpg", src); // ?v cache-buster from the filename, so a replace refetches
    }

    [Fact]
    public void A_recipe_without_a_photo_renders_no_image()
    {
        SeedRecipe("Plain Dish", [("Thing", true, null, null)], "Do.");
        var cut = RenderCookbook();
        Assert.Empty(cut.FindAll("img.cookbook-photo"));
    }

    [Fact]
    public void Adding_a_photo_stores_it_and_shows_it()
    {
        var id = SeedRecipe("Snap Dish", [("Thing", true, null, null)], "Do.");

        var cut = RenderCookbook();
        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1, 2, 3], "photo.jpg", contentType: "image/jpeg"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("img.cookbook-photo")));
        using var db = Db.CreateDbContext();
        Assert.NotNull(db.Recipes.First(r => r.Id == id).ImagePath);
    }

    [Fact]
    public void Removing_a_photo_clears_it()
    {
        var id = SeedRecipe("Photo Dish", [("Thing", true, null, null)], "Do.");
        SetImagePathInDb(id, "recipe-images/hh/abc123.jpg");

        var cut = RenderCookbook();
        cut.Find("button[aria-label='Remove the photo for Photo Dish']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("img.cookbook-photo")));
        using var db = Db.CreateDbContext();
        Assert.Null(db.Recipes.First(r => r.Id == id).ImagePath);
    }

    [Fact]
    public void A_failed_photo_save_shows_a_message_and_leaves_no_orphaned_file()
    {
        SeedRecipe("Photo Dish", [("Thing", true, null, null)], "Do.");
        var cut = RenderCookbook();

        Factory.FailAfter = 0; // SavePhotoAsync's DB context dies AFTER the image file is written
        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1, 2, 3], "photo.jpg", contentType: "image/jpeg"));

        cut.WaitForAssertion(() => Assert.Contains("Couldn't save that photo", cut.Markup));
        // The just-written file was cleaned up on the DB failure — no stray image under the store tree.
        var strays = Directory.Exists(_imageDir)
            ? Directory.GetFiles(_imageDir, "*.jpg", SearchOption.AllDirectories)
            : [];
        Assert.Empty(strays);
    }
}

/// <summary>A scriptable recipe-tag advisor for the cookbook tests — returns whatever <see cref="Next"/>
/// is set to (empty by default, so it's inert for tests that don't touch tags), and counts its calls.</summary>
internal sealed class FakeRecipeTagAdvisor : IRecipeTagAdvisor
{
    public IReadOnlyList<string> Next { get; set; } = [];
    public int Calls { get; private set; }

    public Task<IReadOnlyList<string>> SuggestAsync(
        string recipeName, IReadOnlyList<string> ingredientNames, IReadOnlyList<string> knownTags,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Next);
    }
}
