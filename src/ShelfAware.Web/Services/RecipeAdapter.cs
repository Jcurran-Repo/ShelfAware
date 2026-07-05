using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Recipes;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>
/// <see cref="IRecipeAdapter"/> impl: loads a saved recipe, computes what's on hand (the same edible /
/// not-overdue rule the Recipes page uses), asks the recipe advisor to rewrite it for those ingredients,
/// and saves the result as a variant (ParentRecipeId). One path for both the "Adapt" button and the
/// adapt_recipe chat/voice tool.
/// </summary>
public class RecipeAdapter(IDbContextFactory<ShelfAwareDbContext> dbFactory, IRecipeAdvisor advisor) : IRecipeAdapter
{
    public async Task<AdaptResult> AdaptToOnHandAsync(int recipeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var recipe = await db.Recipes.Include(r => r.Ingredients).Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        if (recipe is null) return new AdaptResult(false, "That recipe couldn't be found.");
        if (recipe.ParentRecipeId is not null)
            return new AdaptResult(false, "That's already an adapted version — adapt the original instead.");

        var products = await db.Products.Where(p => p.IsTracked)
            .Include(p => p.Purchases).Include(p => p.Signals).ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var onHand = products
            .Where(p => p.Category.IsEdible())
            .Where(p => ReplenishmentPredictor.Predict(p, today).Status != PredictionStatus.Overdue)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();
        var excluded = await db.ExcludedFoods.Select(f => f.Value).ToListAsync(cancellationToken);

        var input = new RecipeToAdapt(
            recipe.Name,
            recipe.Blurb,
            recipe.Ingredients.Select(i => new AdaptIngredient(i.Name, i.IsMain)).ToList(),
            recipe.Steps.OrderBy(s => s.Order).Select(s => s.Text).ToList());

        RecipeSuggestion? adapted;
        try
        {
            adapted = await advisor.AdaptAsync(input, onHand, excluded, cancellationToken);
        }
        catch
        {
            return new AdaptResult(false, "Couldn't reach the assistant to adapt that just now.");
        }
        if (adapted is null || string.IsNullOrWhiteSpace(adapted.Name))
            return new AdaptResult(false, $"Couldn't adapt {recipe.Name} right now.");

        var variant = new Recipe
        {
            Name = adapted.Name,
            Blurb = adapted.Blurb,
            SavedAt = DateTimeOffset.Now,
            ParentRecipeId = recipe.Id,
            EstimatedCaloriesPerServing = adapted.CaloriesPerServing,
            Ingredients = adapted.Ingredients
                .Select(i => new RecipeIngredient { Name = i.Name, IsMain = i.IsMain, MatchedProduct = i.MatchedProduct })
                .ToList(),
            Steps = adapted.Steps.Select((t, idx) => new RecipeStep { Order = idx + 1, Text = t }).ToList(),
        };
        db.Recipes.Add(variant);
        await db.SaveChangesAsync(cancellationToken);
        return new AdaptResult(true, $"Saved \"{variant.Name}\" — a version of {recipe.Name} using what you have.", variant.Id);
    }
}
