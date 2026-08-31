using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Components;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Recipes page beyond "Ate it": the suggestion batch (an AI call's results are too expensive
/// to evaporate — persisted until replaced or cleared, with availability marks recomputed live so
/// an old batch stays truthful), makeability and the red-row hints that let a wrong red explain
/// itself, adapt + the swap clouds (generated once, cached), and the ?uses / ?read deep links the
/// hands-free voice chain rides on.
/// </summary>
public class RecipesSuggestAndAdaptTests : PageTestContext
{

    private int SeedProduct(string name, Action<Product>? configure = null)
    {
        using var db = Db.CreateDbContext();
        var product = new Product { Name = name, Category = Category.Meat };
        configure?.Invoke(product);
        db.Products.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    /// <summary>A product the engine sees as ON HAND for recipes: tracked, edible, and Stocked
    /// (bought recently against a learned rhythm).</summary>
    private int SeedStocked(string name) => SeedProduct(name, p => p.Purchases =
    [
        new PurchaseEvent { PurchasedAt = Today.AddDays(-16), Quantity = 1m },
        new PurchaseEvent { PurchasedAt = Today.AddDays(-1), Quantity = 1m },
    ]);

    /// <summary>A product the engine thinks has RUN OUT: overdue on its own rhythm.</summary>
    private int SeedRunOut(string name) => SeedProduct(name, p => p.Purchases =
    [
        new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
        new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
    ]);

    private int SeedRecipe(string name, params RecipeIngredient[] ingredients)
    {
        using var db = Db.CreateDbContext();
        var recipe = new Recipe { Name = name, SavedAt = DateTimeOffset.Now, Ingredients = [.. ingredients] };
        db.Recipes.Add(recipe);
        db.SaveChanges();
        return recipe.Id;
    }

    private IRenderedComponent<Recipes> RenderRecipes()
    {
        var cut = Render<Recipes>();
        cut.WaitForState(() => cut.FindAll("section.panel").Count > 0);
        return cut;
    }

    private void Suggest(IRenderedComponent<Recipes> cut, string request)
    {
        cut.Find("input[aria-label=\"Describe what you're in the mood for\"]").Input(request);
        cut.FindAll("form").First(f => f.QuerySelector("button")?.TextContent.Contains("Get ideas") == true).Submit();
    }

    private static RecipeSuggestion Tacos(string? matched = null) => new(
        "Weeknight Tacos", "Fast and forgiving.",
        [new SuggestedIngredient("ground beef", true, matched), new SuggestedIngredient("cumin", false, null)],
        ["Brown the beef.", "Season and serve."], 450);

    // ------------------------------------------------------------------------- the suggestion batch

    [Fact]
    public async Task A_successful_batch_renders_cards_and_persists_the_snapshot()
    {
        SeedStocked("Ground Beef");
        SuggestionAdvisor.Suggestions = [Tacos(matched: "Ground Beef")];
        var cut = RenderRecipes();

        Suggest(cut, "something fast");

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find(".recipe-card");
            Assert.Contains("Weeknight Tacos", card.TextContent);
            Assert.Contains("~450 cal / serving", card.TextContent);
            // The model's positive match is on hand, so the row is a ✓ and nothing needs grabbing.
            Assert.Contains("You’ve got everything for this.", card.TextContent);
        });
        Assert.Contains("Ideas for “something fast”", cut.Find(".suggestions-head").TextContent);

        // Persisted until replaced or cleared — a navigation or restart must not eat the call.
        var stored = await AppSettings.GetAsync(SettingKeys.LastRecipeSuggestions);
        Assert.NotNull(stored);
        Assert.Contains("Weeknight Tacos", stored);
    }

    [Fact]
    public async Task A_failed_call_keeps_the_previous_batch_on_screen_and_in_storage()
    {
        SuggestionAdvisor.Suggestions = [Tacos()];
        var cut = RenderRecipes();
        Suggest(cut, "first ask");
        cut.WaitForState(() => cut.FindAll(".recipe-card").Count == 1);
        var storedBefore = await AppSettings.GetAsync(SettingKeys.LastRecipeSuggestions);

        SuggestionAdvisor.Throw = new InvalidOperationException("model down");
        Suggest(cut, "second ask");

        cut.WaitForAssertion(() =>
            Assert.Equal("Couldn't get ideas just now — please try again.", cut.Find("p.error").TextContent.Trim()));
        // The old cards survive the failure — replaced only by a SUCCESSFUL new batch.
        Assert.Single(cut.FindAll(".recipe-card"));
        Assert.Equal(storedBefore, await AppSettings.GetAsync(SettingKeys.LastRecipeSuggestions));
    }

    [Fact]
    public void An_empty_answer_reads_as_try_rephrasing_not_as_success()
    {
        SuggestionAdvisor.Suggestions = [];
        var cut = RenderRecipes();

        Suggest(cut, "unicorn stew");

        cut.WaitForAssertion(() =>
            Assert.Equal("No ideas came back — try rephrasing.", cut.Find("p.error").TextContent.Trim()));
        Assert.Empty(cut.FindAll(".recipe-card"));
    }

    [Fact]
    public async Task The_stored_batch_restores_on_load_with_availability_recomputed_live()
    {
        // The snapshot was taken when "Ground Beef" was on hand (a positive MatchedProduct), but
        // the pantry has since emptied. Have/ToGrab are [JsonIgnore] precisely so a restored batch
        // can't replay a stale ✓ — the mark must recompute against TODAY's pantry.
        var snapshotJson = JsonSerializer.Serialize(new
        {
            Request = "tacos night",
            GeneratedAt = DateTimeOffset.Now.AddDays(-2),
            Suggestions = new[] { Tacos(matched: "Ground Beef") },
        });
        await AppSettings.SetAsync(SettingKeys.LastRecipeSuggestions, snapshotJson);

        var cut = RenderRecipes();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Ideas for “tacos night”", cut.Find(".suggestions-head").TextContent);
            var row = cut.Find(".recipe-card .ingredient-list li");
            Assert.Contains("grab", row.GetAttribute("class"));
            Assert.Contains("🛒", row.TextContent);
        });
        Assert.Contains("Grab:", cut.Find(".recipe-grab").TextContent);
    }

    [Fact]
    public async Task A_snapshot_that_no_longer_parses_is_discarded_not_fatal()
    {
        await AppSettings.SetAsync(SettingKeys.LastRecipeSuggestions, "{not json");

        var cut = RenderRecipes();

        // The page renders normally, and the corrupt snapshot is actively cleared so it doesn't
        // fail again on every future load.
        Assert.Empty(cut.FindAll(".recipe-card"));
        // Cleared, which in the store is an empty value rather than a missing row — the same state
        // RestoreSuggestionsAsync reads as "nothing saved". Awaited out here rather than inside
        // WaitForAssertion, whose lambda is synchronous: an async one runs unobserved and pins nothing.
        Assert.True(string.IsNullOrEmpty(await AppSettings.GetAsync(SettingKeys.LastRecipeSuggestions)));
    }

    [Fact]
    public async Task Clear_ideas_removes_the_cards_and_the_stored_batch()
    {
        SuggestionAdvisor.Suggestions = [Tacos()];
        var cut = RenderRecipes();
        Suggest(cut, "anything");
        cut.WaitForState(() => cut.FindAll(".recipe-card").Count == 1);

        cut.Find("button[aria-label='Clear these recipe ideas']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".recipe-card")));
        // Empty, not absent: clearing a setting writes an empty value rather than removing the row.
        Assert.True(string.IsNullOrEmpty(await AppSettings.GetAsync(SettingKeys.LastRecipeSuggestions)));
    }

    [Fact]
    public async Task Saving_a_suggestion_persists_it_and_the_button_locks()
    {
        SuggestionAdvisor.Suggestions = [Tacos()];
        var cut = RenderRecipes();
        Suggest(cut, "anything");
        cut.WaitForState(() => cut.FindAll(".recipe-card").Count == 1);

        cut.FindAll(".recipe-card button").Single(b => b.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() =>
        {
            // The card's button flips to a locked "Saved ✓" — saving twice would split the recipe.
            var saved = cut.FindAll(".recipe-card button").Single(b => b.TextContent.Contains("Saved"));
            Assert.True(saved.HasAttribute("disabled"));
        });

        await using var raw = Db.CreateUnscopedContext();
        var recipe = await raw.Recipes.IgnoreQueryFilters()
            .Include(r => r.Ingredients).Include(r => r.Steps).SingleAsync();
        Assert.Equal("Weeknight Tacos", recipe.Name);
        Assert.Equal(2, recipe.Ingredients.Count);
        Assert.Equal(2, recipe.Steps.Count);
        Assert.Equal(450, recipe.EstimatedCaloriesPerServing);

        // The save is logged and undoable (undo deletes a still-pristine recipe).
        var entry = await raw.ActivityEntries.IgnoreQueryFilters().SingleAsync(e => e.Kind == ActivityKind.RecipeSaved);
        Assert.Equal("Saved recipe: Weeknight Tacos", entry.Summary);
        Assert.Equal(Reversibility.Reversible, entry.Reversibility);
    }

    [Fact]
    public async Task Adding_a_wont_eat_food_records_an_undoable_entry()
    {
        var cut = RenderRecipes();

        cut.Find("input[aria-label=\"Add a food you won't eat\"]").Input("mushrooms");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add").Click();
        cut.WaitForState(() => cut.Find(".tag-list").TextContent.Contains("mushrooms"));

        // A won't-eat change is a reversible soft action — one undo removes it again.
        await using var raw = Db.CreateUnscopedContext();
        var entry = await raw.ActivityEntries.IgnoreQueryFilters().SingleAsync(e => e.Kind == ActivityKind.ExcludedFoodChanged);
        Assert.Equal("Added mushrooms to your won't-eat list", entry.Summary);
        Assert.Equal(Reversibility.Reversible, entry.Reversibility);
    }

    [Fact]
    public void The_wont_eat_list_reaches_every_suggestion_call()
    {
        // The chips are ordinary CRUD; the CONTRACT is that the exclusion actually rides along on
        // the model call — "applies to every suggestion" is this argument, not the chip.
        SeedStocked("Whole Milk");
        SuggestionAdvisor.Suggestions = [Tacos()];
        var cut = RenderRecipes();

        cut.Find("input[aria-label=\"Add a food you won't eat\"]").Input("mushrooms");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add").Click();
        cut.WaitForState(() => cut.Find(".tag-list").TextContent.Contains("mushrooms"));

        Suggest(cut, "dinner");

        cut.WaitForAssertion(() => Assert.NotNull(SuggestionAdvisor.LastExcluded));
        Assert.Equal(["mushrooms"], SuggestionAdvisor.LastExcluded);
        Assert.Contains("Whole Milk", SuggestionAdvisor.LastOnHand!); // grounded in the real catalog
    }

    // ------------------------------------------------------------- makeability and the red-row hints

    [Fact]
    public void Makeability_badges_read_from_the_matcher_both_ways()
    {
        SeedStocked("Chicken Breast Tenderloins");
        SeedRecipe("Ready Dinner", new RecipeIngredient { Name = "chicken breast", IsMain = true });
        SeedRecipe("Blocked Dinner", new RecipeIngredient { Name = "octopus", IsMain = true });
        var cut = RenderRecipes();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".saved-recipes > li");
            var ready = rows.Single(r => r.TextContent.Contains("Ready Dinner"));
            var blocked = rows.Single(r => r.TextContent.Contains("Blocked Dinner"));
            Assert.Contains("Ready to make", ready.TextContent);
            Assert.Contains("Missing items", blocked.TextContent);
        });
    }

    [Fact]
    public void A_recipe_covered_only_by_a_stand_in_reads_makeable_with_a_swap_and_points_at_adapt()
    {
        // You don't own steak, but you own Chuck Roast and marked it "also works as" steak. Every main is
        // covered, so the recipe IS makeable — but the steps are written for a seared steak, and chuck must
        // be braised. So the badge is "Makeable with a swap", NOT "Ready to make"; the ingredient reads ✓
        // but flagged a stand-in; and the row points at Adapt (which now rebuilds the method). This is the
        // whole point of Jordan's "works as = same meal, not same method" — a stand-in never means
        // "cook this as written".
        var chuckId = SeedStocked("Chuck Roast");
        using (var db = Db.CreateDbContext())
        {
            db.ProductSubstitutes.Add(new ProductSubstitute { ProductId = chuckId, Value = "steak" });
            db.SaveChanges();
        }
        SeedRecipe("Steak Dinner", new RecipeIngredient { Name = "steak", IsMain = true });
        var cut = RenderRecipes();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find(".saved-recipes > li");
            Assert.Contains("Makeable with a swap", row.TextContent);
            Assert.DoesNotContain("Ready to make", row.TextContent);
            var ingredient = row.QuerySelector(".ingredient-list li")!;
            Assert.Contains("have", ingredient.GetAttribute("class"));   // it IS covered (✓) …
            Assert.Single(ingredient.QuerySelectorAll(".swap-note"));     // … but flagged a stand-in
            Assert.Contains("Adapt to what I have", row.TextContent);     // the fix is named
        });
    }

    [Fact]
    public async Task A_red_row_covered_by_a_run_out_product_offers_restocked_and_recovers()
    {
        var milkId = SeedRunOut("Whole Milk");
        SeedRecipe("Pancakes", new RecipeIngredient { Name = "whole milk", IsMain = true });
        var cut = RenderRecipes();

        // The red mark explains itself: the engine merely PREDICTS the milk ran out, and the row
        // says so with the correction one tap away — a bare red mark reads as a genuine gap.
        cut.WaitForAssertion(() =>
            Assert.Contains("you may still have Whole Milk — it just looks run-out",
                cut.Find(".saved-recipes").TextContent));

        cut.Find("button[aria-label^='Mark Whole Milk restocked']").Click();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find(".saved-recipes .ingredient-list li");
            Assert.Contains("have", row.GetAttribute("class"));
        });
        await using var raw = Db.CreateUnscopedContext();
        var signal = Assert.Single(await raw.InventorySignals.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SignalKind.Restocked, signal.Kind);
        Assert.Equal(milkId, signal.ProductId);
    }

    [Fact]
    public void A_counted_main_reads_a_confident_check_not_likely()
    {
        // A FRESH count is real evidence — the ✓ is earned, no "likely" hedge.
        SeedProduct("Chicken Breast", p =>
        {
            p.Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-16), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-1), Quantity = 1m },
            ];
            p.TrackQuantity = true;
            p.QuantityOnHand = 3m;
            p.QuantityCountedAt = DateTimeOffset.Now;
        });
        SeedRecipe("Chicken Dinner", new RecipeIngredient { Name = "chicken breast", IsMain = true, MatchedProduct = "Chicken Breast" });
        var cut = RenderRecipes();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find(".saved-recipes .ingredient-list li");
            var cls = row.GetAttribute("class");
            Assert.Contains("have", cls);            // on hand
            Assert.DoesNotContain("likely", cls);    // and CONFIDENT — a fresh count backs it
            Assert.Empty(row.QuerySelectorAll(".likely-note"));
        });
    }

    [Fact]
    public void A_predicted_only_main_reads_likely_because_no_count_backs_it()
    {
        // In stock by the RHYTHM, never counted — the honest render is "likely", not a confident ✓. This
        // is the fix for "the recipe said I had it but I didn't": the guess is shown as a guess.
        SeedStocked("Chicken Breast");
        SeedRecipe("Chicken Dinner", new RecipeIngredient { Name = "chicken breast", IsMain = true, MatchedProduct = "Chicken Breast" });
        var cut = RenderRecipes();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find(".saved-recipes .ingredient-list li");
            var cls = row.GetAttribute("class");
            Assert.Contains("have", cls);
            Assert.Contains("likely", cls);
            Assert.Single(row.QuerySelectorAll(".likely-note"));
        });
    }

    [Fact]
    public async Task Im_out_on_a_have_main_files_an_outnow_and_the_row_goes_missing()
    {
        var id = SeedStocked("Chicken Breast");
        SeedRecipe("Chicken Dinner", new RecipeIngredient { Name = "chicken breast", IsMain = true, MatchedProduct = "Chicken Breast" });
        var cut = RenderRecipes();
        cut.WaitForAssertion(() =>
            Assert.Contains("have", cut.Find(".saved-recipes .ingredient-list li").GetAttribute("class")));

        // "The recipe said I have this, but I don't" — the have-side correction the feature was missing.
        cut.Find("button[aria-label^='Mark chicken breast out']").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("grab", cut.Find(".saved-recipes .ingredient-list li").GetAttribute("class")));
        // A REAL OutNow was filed — it STICKS (dashboard + grocery see it, undoable via History), not a
        // display-only mark that evaporates on reload.
        await using var raw = Db.CreateUnscopedContext();
        var signal = Assert.Single(await raw.InventorySignals.IgnoreQueryFilters().Where(s => s.ProductId == id).ToListAsync());
        Assert.Equal(SignalKind.OutNow, signal.Kind);
    }

    [Fact]
    public void After_im_out_the_row_says_you_marked_it_out_not_that_it_just_looks_run_out()
    {
        // Gate B-1: after you DECLARE it out, the row must not blame a soft prediction ("it just looks
        // run-out") — that contradicts your own tap (one prediction, one story).
        SeedStocked("Chicken Breast");
        SeedRecipe("Chicken Dinner", new RecipeIngredient { Name = "chicken breast", IsMain = true, MatchedProduct = "Chicken Breast" });
        var cut = RenderRecipes();
        cut.WaitForAssertion(() =>
            Assert.Contains("have", cut.Find(".saved-recipes .ingredient-list li").GetAttribute("class")));

        cut.Find("button[aria-label^='Mark chicken breast out']").Click();

        cut.WaitForAssertion(() =>
        {
            var saved = cut.Find(".saved-recipes").TextContent;
            Assert.Contains("you marked Chicken Breast out", saved);
            Assert.DoesNotContain("it just looks run-out", saved);
        });
    }

    [Fact]
    public void Im_out_on_a_bought_today_item_is_inert_and_says_why_rather_than_doing_nothing()
    {
        // Gate B-2: bought TODAY → Stocked, shows ✓ + "I'm out". But an OutNow dated today is inert
        // (§6.6 same-day tie), so nothing changes — the tap must not be a SILENT no-op.
        SeedProduct("Chicken Breast", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-14), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today, Quantity = 1m }, // bought today → same-day tie
        ]);
        SeedRecipe("Chicken Dinner", new RecipeIngredient { Name = "chicken breast", IsMain = true, MatchedProduct = "Chicken Breast" });
        var cut = RenderRecipes();
        cut.WaitForAssertion(() =>
            Assert.Contains("have", cut.Find(".saved-recipes .ingredient-list li").GetAttribute("class")));

        cut.Find("button[aria-label^='Mark chicken breast out']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("won't take effect until tomorrow", cut.Markup);
            // …and the row is honest that nothing changed — it stays on hand (the inert OutNow didn't fire).
            Assert.Contains("have", cut.Find(".saved-recipes .ingredient-list li").GetAttribute("class"));
        });
    }

    [Fact]
    public void Im_out_with_a_second_same_food_product_names_the_alternative_not_a_false_inert_note()
    {
        // Gate Medium: a main grounded to "Chicken Breast", with BOTH "Chicken Breast" and a second
        // same-food product ("Chicken Breast Tenderloins") on hand. "I'm out" marks only the grounded
        // product (its ✓ is about that one); the tenderloins still cover the ingredient, so the row stays
        // green. The note must tell the TRUTH — the OutNow fired, you have an alternative — not the false
        // "won't take effect until tomorrow" it used to infer from the row merely staying green.
        SeedStocked("Chicken Breast");
        SeedStocked("Chicken Breast Tenderloins");
        SeedRecipe("Chicken Dinner", new RecipeIngredient { Name = "chicken breast", IsMain = true, MatchedProduct = "Chicken Breast" });
        var cut = RenderRecipes();
        cut.WaitForAssertion(() =>
            Assert.Contains("have", cut.Find(".saved-recipes .ingredient-list li").GetAttribute("class")));

        cut.Find("button[aria-label^='Mark chicken breast out']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("won't take effect", cut.Markup);               // the false inert note is gone
            Assert.Contains("still have Chicken Breast Tenderloins", cut.Markup);  // the honest one names the alternative
            Assert.Contains("have", cut.Find(".saved-recipes .ingredient-list li").GetAttribute("class")); // row stays green
        });
    }

    [Fact]
    public void The_im_out_note_clears_when_another_row_is_restocked()
    {
        // imOutNote is a page-level status line; once "I'm out" sets it, an unrelated Restocked on a
        // different row must not leave the stale note contradicting that new act.
        SeedProduct("Chicken Breast", p => p.Purchases =
        [
            new PurchaseEvent { PurchasedAt = Today.AddDays(-14), Quantity = 1m },
            new PurchaseEvent { PurchasedAt = Today, Quantity = 1m }, // bought today → "I'm out" is inert → a note
        ]);
        SeedRunOut("Rice"); // run-out → its row offers a Restocked button
        SeedRecipe("Chicken and Rice",
            new RecipeIngredient { Name = "chicken breast", IsMain = true, MatchedProduct = "Chicken Breast" },
            new RecipeIngredient { Name = "rice", IsMain = true, MatchedProduct = "Rice" });
        var cut = RenderRecipes();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label^='Mark chicken breast out']")));

        cut.Find("button[aria-label^='Mark chicken breast out']").Click();
        cut.WaitForAssertion(() => Assert.Contains("won't take effect", cut.Markup)); // the note is up

        cut.Find("button[aria-label^='Mark Rice restocked']").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("won't take effect", cut.Markup)); // …cleared by the Restocked
    }

    [Fact]
    public async Task A_red_row_covered_by_an_untracked_product_offers_track_it()
    {
        var riceId = SeedProduct("Basmati Rice", p => { p.Category = Category.Pantry; p.IsTracked = false; });
        SeedRecipe("Rice Bowl", new RecipeIngredient { Name = "basmati rice", IsMain = true });
        var cut = RenderRecipes();

        cut.WaitForAssertion(() =>
            Assert.Contains("you have this as Basmati Rice, but it's untracked",
                cut.Find(".saved-recipes").TextContent));

        cut.Find("button[aria-label='Track Basmati Rice again']").Click();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find(".saved-recipes .ingredient-list li");
            Assert.Contains("have", row.GetAttribute("class"));
        });
        await using var raw = Db.CreateUnscopedContext();
        Assert.True((await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == riceId)).IsTracked);
    }

    [Fact]
    public async Task Add_missing_to_list_sends_only_the_gaps_and_dedupes_repeats()
    {
        SeedStocked("Chicken Breast Tenderloins");
        SeedRecipe("Stir Fry",
            new RecipeIngredient { Name = "chicken breast", IsMain = true },
            new RecipeIngredient { Name = "snow peas", IsMain = true });
        var cut = RenderRecipes();
        cut.WaitForState(() => cut.FindAll(".saved-recipes > li").Count == 1);

        cut.Find("button[aria-label^='Add missing ingredients']").Click();
        cut.WaitForAssertion(() =>
            Assert.Contains("Added 1 item to your grocery list.", cut.Find(".chat-reply").TextContent));

        // The covered main stays off the list — only the genuine gap travels.
        await using (var raw = Db.CreateUnscopedContext())
        {
            var extra = Assert.Single(await raw.GroceryExtras.IgnoreQueryFilters().ToListAsync());
            Assert.Equal("snow peas", extra.Name);
        }

        cut.Find("button[aria-label^='Add missing ingredients']").Click();
        cut.WaitForAssertion(() =>
            Assert.Contains("Those are already on your grocery list.", cut.Find(".chat-reply").TextContent));
        await using (var raw = Db.CreateUnscopedContext())
        {
            Assert.Single(await raw.GroceryExtras.IgnoreQueryFilters().ToListAsync());
            // Through the store, so the add is logged + undoable; the second (all-dupes) click records nothing.
            Assert.Single(await raw.ActivityEntries.IgnoreQueryFilters()
                .Where(e => e.Kind == ActivityKind.GroceryExtrasAdded).ToListAsync());
        }
    }

    // ------------------------------------------------------------------------ adapt + swap clouds

    [Fact]
    public void Adapt_reports_the_adapters_answer_and_asks_for_the_whole_recipe()
    {
        var recipeId = SeedRecipe("Beef Stew", new RecipeIngredient { Name = "beef chuck", IsMain = true });
        Adapter.Next = new ShelfAware.Core.Recipes.AdaptResult(true, "Saved a version using what you have.");
        var cut = RenderRecipes();

        cut.Find("button[aria-label^='Adapt']").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("Saved a version using what you have.", cut.Find(".chat-reply").TextContent));
        Assert.Equal([(recipeId, (IngredientSwap?)null)], Adapter.Asked);
    }

    [Fact]
    public async Task The_swap_cloud_generates_once_and_reopens_from_its_cache()
    {
        SeedRecipe("Roast", new RecipeIngredient { Name = "chicken breast", IsMain = true });
        AlternativesAdvisor.Alternatives = ["chicken thighs", "turkey breast"];
        var cut = RenderRecipes();
        cut.WaitForState(() => cut.FindAll(".alt-swap").Count == 1);

        cut.Find(".alt-swap").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".alt-cloud .alt-bubble").Count));
        Assert.Equal(1, AlternativesAdvisor.Calls);

        // Close, reopen: the forms come back from RecipeIngredient.AlternativesJson — no second call.
        cut.Find(".alt-swap").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".alt-cloud")));
        cut.Find(".alt-swap").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".alt-cloud .alt-bubble").Count));
        Assert.Equal(1, AlternativesAdvisor.Calls);

        await using var raw = Db.CreateUnscopedContext();
        var stored = (await raw.RecipeIngredients.IgnoreQueryFilters().SingleAsync()).AlternativesJson;
        Assert.Contains("chicken thighs", stored);
    }

    [Fact]
    public void Curated_stand_ins_lead_the_cloud_and_a_bubble_click_adapts_to_that_form()
    {
        // The user's own "also works as" product comes first; the AI's generic form of the same
        // product dedupes behind it; a genuinely different AI form stays.
        var recipeId = SeedRecipe("Roast", new RecipeIngredient { Name = "chicken breast", IsMain = true });
        SeedStocked("Chicken Thighs");
        using (var db = Db.CreateDbContext())
        {
            var thighs = db.Products.Single(p => p.Name == "Chicken Thighs");
            db.ProductSubstitutes.Add(new ProductSubstitute { ProductId = thighs.Id, Value = "chicken breast" });
            db.SaveChanges();
        }
        AlternativesAdvisor.Alternatives = ["chicken thighs", "turkey breast"];
        Adapter.Next = new ShelfAware.Core.Recipes.AdaptResult(true, "Adapted.");
        var cut = RenderRecipes();
        cut.WaitForState(() => cut.FindAll(".alt-swap").Count == 1);

        cut.Find(".alt-swap").Click();

        cut.WaitForAssertion(() =>
        {
            var bubbles = cut.FindAll(".alt-cloud .alt-bubble");
            Assert.Equal(2, bubbles.Count);
            Assert.Contains("Chicken Thighs", bubbles[0].TextContent); // curated first, on hand → ✓
            Assert.Contains("✓", bubbles[0].TextContent);
            Assert.Contains("turkey breast", bubbles[1].TextContent);  // AI form, not owned → 🛒
            Assert.Contains("🛒", bubbles[1].TextContent);
        });

        cut.FindAll(".alt-cloud .alt-bubble")[1].Click();

        cut.WaitForAssertion(() => Assert.Single(Adapter.Asked));
        Assert.Equal((recipeId, new IngredientSwap("chicken breast", "turkey breast")), Adapter.Asked.Single());
    }

    // --------------------------------------------------------------- pick for me / delete / links

    [Fact]
    public void Pick_for_me_draws_only_from_eaten_and_makeable_recipes()
    {
        SeedStocked("Chicken Breast Tenderloins");
        using (var db = Db.CreateDbContext())
        {
            db.Recipes.Add(new Recipe
            {
                Name = "Old Faithful", SavedAt = DateTimeOffset.Now, TimesEaten = 3,
                Ingredients = [new RecipeIngredient { Name = "chicken breast", IsMain = true }],
            });
            db.Recipes.Add(new Recipe
            {
                Name = "Never Tried", SavedAt = DateTimeOffset.Now,
                Ingredients = [new RecipeIngredient { Name = "chicken breast", IsMain = true }],
            });
            db.Recipes.Add(new Recipe
            {
                Name = "Eaten But Blocked", SavedAt = DateTimeOffset.Now, TimesEaten = 5,
                Ingredients = [new RecipeIngredient { Name = "octopus", IsMain = true }],
            });
            db.SaveChanges();
        }
        var cut = RenderRecipes();
        cut.WaitForState(() => cut.FindAll(".saved-recipes > li").Count == 3);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Pick for me")).Click();

        // One recipe qualifies (eaten AND makeable), so the pick is deterministic — the pool rule
        // is what's under test, not the dice.
        cut.WaitForAssertion(() =>
            Assert.Contains("Tonight: Old Faithful", cut.Find(".picked-banner").TextContent.Replace("\n", " ")));
    }

    [Fact]
    public async Task Deleting_a_saved_recipe_removes_it_for_good()
    {
        SeedRecipe("Mistake", new RecipeIngredient { Name = "anything", IsMain = true });
        var cut = RenderRecipes();
        cut.WaitForState(() => cut.FindAll(".saved-recipes > li").Count == 1);

        cut.Find("button[aria-label='Delete Mistake']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".saved-recipes li")));
        await using var raw = Db.CreateUnscopedContext();
        Assert.Empty(await raw.Recipes.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public void The_uses_filter_matches_variants_on_their_own_ingredients_with_the_original_as_reference()
    {
        var chickenId = SeedStocked("Chicken Breast Tenderloins");
        var turkeyId = SeedStocked("Turkey Legs");
        int originalId;
        using (var db = Db.CreateDbContext())
        {
            var original = new Recipe
            {
                Name = "Chicken Roast", SavedAt = DateTimeOffset.Now.AddDays(-2),
                Ingredients = [new RecipeIngredient { Name = "chicken breast", IsMain = true, MatchedProduct = "Chicken Breast Tenderloins" }],
            };
            db.Recipes.Add(original);
            db.SaveChanges();
            originalId = original.Id;
            db.Recipes.Add(new Recipe
            {
                Name = "Turkey Roast", SavedAt = DateTimeOffset.Now, ParentRecipeId = originalId,
                Ingredients = [new RecipeIngredient { Name = "turkey legs", IsMain = true, MatchedProduct = "Turkey Legs" }],
            });
            db.SaveChanges();
        }

        // Adapt swapped the main, so the VARIANT uses turkey while its original never did — the
        // variant is the result; the original rides along only as muted context.
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/recipes?uses={turkeyId}");
        var cut = RenderRecipes();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Showing recipes that use Turkey Legs", cut.Find(".filter-banner").TextContent);
            var reference = cut.Find(".recipe-reference");
            Assert.Contains("Chicken Roast", reference.TextContent);
            Assert.Contains("for reference", reference.TextContent);
            var variant = cut.Find(".recipe-variant");
            Assert.Contains("Turkey Roast", variant.TextContent);
        });
        // Reference rows are context, not results — the count and the voice agent's list skip them.
        Assert.Contains("— 1", cut.FindAll("h2").Single(h => h.TextContent.Contains("Saved recipes")).TextContent);
        Assert.DoesNotContain("Chicken Roast", Coordinator.ScreenContext);
        Assert.Contains("1. Turkey Roast", Coordinator.ScreenContext);
    }

    [Fact]
    public void The_read_deep_link_starts_the_hands_free_reader_and_strips_itself()
    {
        int recipeId;
        using (var db = Db.CreateDbContext())
        {
            var recipe = new Recipe
            {
                Name = "Read Me", SavedAt = DateTimeOffset.Now,
                Ingredients = [new RecipeIngredient { Name = "anything", IsMain = true }],
                Steps = [new RecipeStep { Order = 1, Text = "Step one." }],
            };
            db.Recipes.Add(recipe);
            db.SaveChanges();
            recipeId = recipe.Id;
        }

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/recipes?read={recipeId}");
        var cut = Render<Recipes>();

        // The reader mounts (stubbed here — its own tests cover the audio), and ?read is consumed:
        // it's a one-shot command, not view state, so a refresh must not surprise-relaunch a mic.
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindComponents<Bunit.TestDoubles.Stub<RecipeReadAloud>>());
            Assert.EndsWith("/recipes", nav.Uri);
        });
    }

    [Fact]
    public async Task The_screen_context_publishes_display_order_and_clears_on_dispose()
    {
        SeedRecipe("First Dish", new RecipeIngredient { Name = "a", IsMain = true });
        SeedRecipe("Second Dish", new RecipeIngredient { Name = "b", IsMain = true });
        var cut = RenderRecipes();

        // Most recently saved renders first — and the voice agent's positional list must match the
        // screen exactly, or "read me the second one" reads the wrong recipe.
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("1. Second Dish", Coordinator.ScreenContext);
            Assert.Contains("2. First Dish", Coordinator.ScreenContext);
        });

        await DisposeComponentsAsync();
        Assert.Null(Coordinator.ScreenContext); // must not leak to whatever page renders next
    }
}
