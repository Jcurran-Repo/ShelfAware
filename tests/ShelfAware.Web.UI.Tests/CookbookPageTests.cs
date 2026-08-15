using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;

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
}
