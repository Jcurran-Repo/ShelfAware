using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The recipe adapter on real EF/SQLite with a faked advisor: covers saving a variant, the content-based
/// dedupe (re-adapting updates in place), the chosen-swap guard (a swap the model ignores is rejected,
/// nothing saved), and the refusal to adapt a variant of a variant.
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
        return new RecipeAdapter(_db, advisor, NullLogger<RecipeAdapter>.Instance);
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
    public async Task A_chosen_swap_the_model_ignores_is_rejected_and_saves_nothing()
    {
        var parentId = await SeedRecipe("Pan-Seared Chicken", "chicken breast");
        // The user picked thighs, but the model came back with tenderloins — don't save a mislabeled variant.
        var adapter = Adapter(Suggestion("Chicken Tenderloin Skillet", "chicken tenderloins"), out var advisor);

        var result = await adapter.AdaptToOnHandAsync(parentId, new IngredientSwap("chicken breast", "chicken thighs"));

        Assert.False(result.Success);
        Assert.Contains("chicken thighs", result.Message);
        Assert.Equal("Use chicken thighs in place of chicken breast.", advisor.LastPreference); // the pick reached the model
        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.Recipes.Where(r => r.ParentRecipeId == parentId).ToListAsync());
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
    public async Task Adapting_a_variant_is_refused()
    {
        var parentId = await SeedRecipe("Pan-Seared Chicken", "chicken breast");
        int variantId;
        await using (var db = _db.CreateDbContext())
        {
            var v = new Recipe { Name = "A variant", SavedAt = DateTimeOffset.Now, ParentRecipeId = parentId };
            db.Recipes.Add(v);
            await db.SaveChangesAsync();
            variantId = v.Id;
        }
        var adapter = Adapter(Suggestion("Whatever", "beef"), out _);

        var result = await adapter.AdaptToOnHandAsync(variantId);

        Assert.False(result.Success);
        Assert.Contains("already an adapted", result.Message);
    }
}
