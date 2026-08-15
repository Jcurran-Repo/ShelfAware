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
/// The Cookbook — an accessible carousel over saved recipes with read-aloud, print, and a product
/// filter. The rules under test: recipes page alphabetically and Prev/Next walk them with a "which of
/// how many" announcement; the "Ready to make"/"Missing items" chip and the ✓/🛒 marks agree with the
/// Recipes page (same PantryOnHand + IngredientMatcher); the ?uses filter scopes the deck to recipes
/// grounded to that product (Recipe.Uses — the one shared definition) and the dropdown navigates there;
/// the two print buttons choose their print-only surface and fire window.print; and the products list
/// prints every ingredient with its amount, ticking the ones already on hand.
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
            cut.FindAll(".cookbook-carousel").Count > 0
            || cut.Markup.Contains("cookbook is empty")
            || cut.Markup.Contains("No saved recipes use"));
        return cut;
    }

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    // ------------------------------------------------------------------ empty / browse / order

    [Fact]
    public void An_empty_cookbook_points_at_Recipes_and_shows_no_carousel()
    {
        var cut = RenderCookbook();

        Assert.Contains("cookbook is empty", cut.Markup);
        Assert.Empty(cut.FindAll(".cookbook-carousel"));
    }

    [Fact]
    public void Recipes_page_alphabetically_and_Prev_Next_walk_the_deck()
    {
        SeedRecipe("Zucchini Bake", [("Zucchini", true, null, null)], "Bake it.");
        SeedRecipe("Apple Crisp", [("Apples", true, null, null)], "Bake it.");

        var cut = RenderCookbook();

        // Alphabetical, not save order: "Apple Crisp" is page one, and Prev is disabled at the start.
        Assert.Contains("Apple Crisp", cut.Find(".cookbook-position").TextContent);
        Assert.Contains("1 of 2", Collapsed(cut.Find(".cookbook-position")));
        Assert.Equal("Apple Crisp", cut.Find(".cookbook-page h2").TextContent);
        Assert.True(cut.Find("button[aria-label='Previous recipe']").HasAttribute("disabled"));

        cut.Find("button[aria-label='Next recipe']").Click();

        Assert.Contains("Zucchini Bake", cut.Find(".cookbook-position").TextContent);
        Assert.Contains("2 of 2", Collapsed(cut.Find(".cookbook-position")));
        Assert.True(cut.Find("button[aria-label='Next recipe']").HasAttribute("disabled"));
    }

    [Fact]
    public void Arrow_keys_page_the_carousel()
    {
        SeedRecipe("Apple Crisp", [("Apples", true, null, null)], "Bake it.");
        SeedRecipe("Banana Bread", [("Bananas", true, null, null)], "Bake it.");

        var cut = RenderCookbook();
        var carousel = cut.Find(".cookbook-carousel");

        carousel.KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        Assert.Contains("Banana Bread", cut.Find(".cookbook-position").TextContent);

        cut.Find(".cookbook-carousel").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        Assert.Contains("Apple Crisp", cut.Find(".cookbook-position").TextContent);
    }

    // ------------------------------------------------------------------ makeability marks

    [Fact]
    public void A_recipe_you_can_make_reads_ready_and_ticks_its_on_hand_main()
    {
        SeedProduct("Chicken Breast");
        SeedRecipe("Chicken Dinner", [("Chicken Breast", true, "Chicken Breast", "2 breasts")], "Cook it.");

        var cut = RenderCookbook();

        Assert.Contains("Ready to make", cut.Find(".chip-stocked").TextContent);
        Assert.Contains("✓", cut.Find(".ingredient-list li.have").TextContent);
        Assert.Empty(cut.FindAll(".ingredient-list li.grab"));
    }

    [Fact]
    public void A_recipe_missing_a_main_reads_missing_items()
    {
        SeedRecipe("Tofu Scramble", [("Tofu", true, "Tofu", null)], "Cook it.");

        var cut = RenderCookbook();

        Assert.Contains("Missing items", cut.Find(".chip-unknown").TextContent);
        Assert.Contains("🛒", cut.Find(".ingredient-list li.grab").TextContent);
        Assert.Empty(cut.FindAll(".ingredient-list li.have"));
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
        Assert.Equal("Chicken Dinner", cut.Find(".cookbook-page h2").TextContent);
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
        Assert.Empty(cut.FindAll(".cookbook-carousel"));
    }

    [Fact]
    public void Choosing_a_product_in_the_filter_navigates_to_that_filter()
    {
        var chickenId = SeedProduct("Chicken Breast");
        SeedRecipe("Chicken Dinner", [("Chicken Breast", true, "Chicken Breast", null)], "Cook it.");

        var cut = RenderCookbook();
        cut.Find(".cookbook-product-filter select").Change(chickenId.ToString());

        Assert.EndsWith($"/cookbook?uses={chickenId}", Nav.Uri);
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

        cut.Find("button[aria-label='Next recipe']").Click(); // → "No Steps"

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
        Assert.Equal("Pasta Night", cut.Find(".cookbook-page h2").TextContent);
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
