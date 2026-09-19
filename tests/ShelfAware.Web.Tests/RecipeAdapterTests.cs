using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The recipe adapter on real EF/SQLite with a faked advisor: covers saving a variant, the content-based
/// dedupe (re-adapting updates in place), the chosen-swap guard (a swap the model ignores is SAVED and
/// LABELLED, not discarded), the curated substitutes riding along to the advisor, and re-rooting (adapting a
/// variant bases on its content but saves as a sibling under the original — never a chain).
/// </summary>
public class RecipeAdapterTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedRecipe(string name, params string[] mains)
    {
        await using var db = _db.CreateDbContext();
        var recipe = new Recipe
        {
            Name = name,
            SavedAt = DateTimeOffset.Now,
            Ingredients = mains.Select(m => new RecipeIngredient { Name = m, IsMain = true }).ToList(),
            Steps = [new RecipeStep { Order = 1, Text = "Cook it." }],
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    private static RecipeSuggestion Suggestion(string name, params string[] mains) =>
        new(name, "A one-pan dinner.", mains.Select(m => new SuggestedIngredient(m, true, null)).ToList(),
            ["Cook the main ingredient through.", "Serve."], 400);

    private RecipeAdapter Adapter(RecipeSuggestion? adaptResult, out FakeRecipeAdvisor advisor)
    {
        advisor = new FakeRecipeAdvisor(adaptResult);
        return new RecipeAdapter(_db, advisor, new FakeAppSettings(), UndoTesting.Log(_db), NullLogger<RecipeAdapter>.Instance);
    }

    [Fact]
    public async Task Adapt_saves_the_result_as_a_variant_of_the_parent()
    {
        var parentId = await SeedRecipe("Pan-Seared Chicken", "chicken breast");
        var adapter = Adapter(Suggestion("Pan-Seared Chicken Thighs", "chicken thighs"), out _);

        var result = await adapter.AdaptToOnHandAsync(parentId);

        Assert.True(result.Success);
        await using var db = _db.CreateDbContext();
        var variant = await db.Recipes.Include(r => r.Ingredients).Include(r => r.Steps)
            .SingleAsync(r => r.ParentRecipeId == parentId);
        Assert.Equal("Pan-Seared Chicken Thighs", variant.Name);
        Assert.Contains(variant.Ingredients, i => i.Name == "chicken thighs");
        Assert.NotEmpty(variant.Steps);

        // The adapt logs an undoable entry, naming the family and the variant (undo deletes the variant).
        var entry = await db.ActivityEntries.SingleAsync(e => e.Kind == ActivityKind.RecipeAdapted);
        Assert.Equal("Adapted Pan-Seared Chicken → Pan-Seared Chicken Thighs", entry.Summary);
        Assert.Equal(Reversibility.Reversible, entry.Reversibility);
    }

    [Fact]
    public async Task Re_adapting_to_the_same_mains_replaces_the_variant_instead_of_duplicating()
    {
        var parentId = await SeedRecipe("Pan-Seared Chicken", "chicken breast");
        // The advisor always returns the same tenderloins result — a second adapt must not pile up.
        var adapter = Adapter(Suggestion("Chicken Tenderloin Skillet", "chicken tenderloins"), out _);

        await adapter.AdaptToOnHandAsync(parentId);
        await adapter.AdaptToOnHandAsync(parentId);

        await using var db = _db.CreateDbContext();
        Assert.Single(await db.Recipes.Where(r => r.ParentRecipeId == parentId).ToListAsync());
        // The replaced variant's ingredients were cascade-deleted, not orphaned: parent's 1 main + the
        // one surviving variant's 1 main = 2 rows.
        Assert.Equal(2, await db.RecipeIngredients.CountAsync());
    }

    [Fact]
    public async Task The_on_hand_list_carries_each_products_also_works_as_substitutes()
    {
        var parentId = await SeedRecipe("Pan-Seared Chicken", "chicken breast");
        await using (var db = _db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Chicken Breast Tenderloins",
                Category = Category.Meat,
                IsTracked = true,
                Substitutes = [new ProductSubstitute { Value = "chicken breast" }, new ProductSubstitute { Value = "chicken cutlet" }],
            });
            await db.SaveChangesAsync();
        }
        var adapter = Adapter(Suggestion("Chicken Tenderloin Skillet", "chicken tenderloins"), out var advisor);

        await adapter.AdaptToOnHandAsync(parentId);

        // The advisor must see the user's curated substitution matrix, not bare product names.
        var tenderloins = advisor.LastOnHand!.Single(p => p.Name == "Chicken Breast Tenderloins");
        Assert.Equal(new[] { "chicken breast", "chicken cutlet" }, tenderloins.AlsoWorksAs);
    }

    /// <summary>
    /// ⚠️ This test asserted the OPPOSITE until 2026-09-19, under the name
    /// "a chosen swap the model ignores is rejected and saves nothing". The behaviour it pinned was:
    /// discard the adaptation, tell the household "I couldn't make a {form} version this time — give it
    /// another try", and keep the charge. That is paid work thrown away plus an invitation to pay again,
    /// and nobody — household or operator — could see what the model had actually produced.
    /// <para>Jordan's call: asking is what is paid for. They asked for the swap, so the act is charged;
    /// what we owe them is the recipe, an honest label, and a way to report it. The variant is saved, the
    /// message says it does not use the form they picked, the blurb carries that note so the row is still
    /// self-describing later, and the page offers a pre-filled bug report.</para>
    /// </summary>
    [Fact]
    public async Task A_chosen_swap_the_model_ignores_is_saved_labelled_rather_than_thrown_away()
    {
        var parentId = await SeedRecipe("Pan-Seared Chicken", "chicken breast");
        // The user picked thighs, but the model came back with tenderloins.
        var adapter = Adapter(Suggestion("Chicken Tenderloin Skillet", "chicken tenderloins"), out var advisor);

        var result = await adapter.AdaptToOnHandAsync(parentId, new IngredientSwap("chicken breast", "chicken thighs"));

        Assert.True(result.Success);
        Assert.Equal("chicken thighs", result.SwapIgnored);
        Assert.Contains("chicken thighs", result.Message);
        Assert.Equal("Use chicken thighs in place of chicken breast.", advisor.LastPreference); // the pick reached the model

        await using var db = _db.CreateDbContext();
        var variant = Assert.Single(await db.Recipes.Where(r => r.ParentRecipeId == parentId).ToListAsync());
        Assert.Equal(result.VariantId, variant.Id);
        // ⚠️ The label travels with the ROW. The message is read once; this variant sits in the cookbook
        // indefinitely, and without this a household finds a tenderloin recipe where they asked for thighs
        // with nothing to tell them which side got it wrong.
        Assert.Contains("chicken thighs", variant.Blurb!);
        // Undoable, because it saved something real this time.
        Assert.Single(await db.ActivityEntries.Where(e => e.Kind == ActivityKind.RecipeAdapted).ToListAsync());
    }

    [Fact]
    public async Task A_swap_the_model_honors_is_saved_with_no_label_and_no_apology()
    {
        // The other side, so the label cannot quietly become unconditional.
        var parentId = await SeedRecipe("Seared Chicken", "chicken breast");
        var adapter = Adapter(Suggestion("Seared Chicken Thighs", "chicken thighs"), out _);

        var result = await adapter.AdaptToOnHandAsync(parentId, new IngredientSwap("chicken breast", "chicken thighs"));

        Assert.True(result.Success);
        Assert.Null(result.SwapIgnored);
        await using var db = _db.CreateDbContext();
        var variant = Assert.Single(await db.Recipes.Where(r => r.ParentRecipeId == parentId).ToListAsync());
        Assert.DoesNotContain("doesn't use", variant.Blurb ?? "");
    }

    [Fact]
    public async Task A_chosen_swap_the_model_honors_is_saved()
    {
        var parentId = await SeedRecipe("Pan-Seared Chicken", "chicken breast");
        var adapter = Adapter(Suggestion("Pan-Seared Chicken Thighs", "chicken thighs"), out _);

        var result = await adapter.AdaptToOnHandAsync(parentId, new IngredientSwap("chicken breast", "chicken thighs"));

        Assert.True(result.Success);
        await using var db = _db.CreateDbContext();
        Assert.Single(await db.Recipes.Where(r => r.ParentRecipeId == parentId).ToListAsync());
    }

    [Fact]
    public async Task Adapting_a_variant_bases_on_its_content_but_re_roots_under_the_original()
    {
        var parentId = await SeedRecipe("Pan-Seared Chicken", "chicken breast");
        int variantId;
        await using (var db = _db.CreateDbContext())
        {
            var v = new Recipe
            {
                Name = "Pan-Seared Chicken Thighs",
                SavedAt = DateTimeOffset.Now,
                ParentRecipeId = parentId,
                Ingredients = [new RecipeIngredient { Name = "chicken thighs", IsMain = true }],
                Steps = [new RecipeStep { Order = 1, Text = "Cook the thighs." }],
            };
            db.Recipes.Add(v);
            await db.SaveChangesAsync();
            variantId = v.Id;
        }
        var adapter = Adapter(Suggestion("Chicken Tenderloin Skillet", "chicken tenderloins"), out var advisor);

        var result = await adapter.AdaptToOnHandAsync(variantId);

        Assert.True(result.Success);
        // The variant's own content is what the advisor was asked to rewrite...
        Assert.Equal("Pan-Seared Chicken Thighs", advisor.LastRecipe!.Name);
        Assert.Contains(advisor.LastRecipe.Ingredients, i => i.Name == "chicken thighs");
        // ...and the family is named after the ORIGINAL in the reply, not the variant.
        Assert.Contains("a version of Pan-Seared Chicken using", result.Message);
        // The new variant is a SIBLING under the original, not a child of the variant — no chains.
        await using var check = _db.CreateDbContext();
        var newVariant = await check.Recipes.SingleAsync(r => r.Name == "Chicken Tenderloin Skillet");
        Assert.Equal(parentId, newVariant.ParentRecipeId);
    }

    [Fact]
    public async Task Re_adapting_a_variant_to_the_same_mains_replaces_it_instead_of_stacking()
    {
        var parentId = await SeedRecipe("Pan-Seared Chicken", "chicken breast");
        var adapter = Adapter(Suggestion("Chicken Tenderloin Skillet", "chicken tenderloins"), out _);
        await adapter.AdaptToOnHandAsync(parentId); // creates the tenderloins variant
        int variantId;
        await using (var db = _db.CreateDbContext())
            variantId = (await db.Recipes.SingleAsync(r => r.ParentRecipeId == parentId)).Id;

        var result = await adapter.AdaptToOnHandAsync(variantId); // adapt the variant; same mains come back

        Assert.True(result.Success);
        await using var check = _db.CreateDbContext();
        // The signature dedupe keys on the original parent, so the stale twin was replaced, not stacked.
        Assert.Single(await check.Recipes.Where(r => r.ParentRecipeId == parentId).ToListAsync());
    }
}
