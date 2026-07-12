using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>
/// <see cref="IRecipeAdapter"/> impl: loads a saved recipe, computes what's on hand (via the shared
/// <see cref="PantryOnHand"/> rule), asks the recipe advisor to rewrite it for those ingredients, and
/// saves the result as a variant (ParentRecipeId). One path for both the "Adapt" button and the
/// adapt_recipe chat/voice tool. Re-adapting to the same result updates in place instead of duplicating.
/// Adapting a VARIANT re-roots: its own content is the base the advisor rewrites, but the result saves
/// as another sibling under the original, so the family stays a flat group — never a chain.
/// </summary>
public class RecipeAdapter(
    IHouseholdDbFactory dbFactory, IRecipeAdvisor advisor, ILogger<RecipeAdapter> logger) : IRecipeAdapter
{
    public async Task<AdaptResult> AdaptToOnHandAsync(int recipeId, IngredientSwap? swap = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var recipe = await db.Recipes.Include(r => r.Ingredients).Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);
        if (recipe is null) return new AdaptResult(false, "That recipe couldn't be found.");
        // Adapting a variant is allowed — its content is the base — but the result re-roots under the
        // ORIGINAL recipe, so variants stay one flat group and the signature dedupe below sees them all.
        var parentId = recipe.ParentRecipeId ?? recipe.Id;
        var familyName = recipe.ParentRecipeId is null
            ? recipe.Name
            : await db.Recipes.Where(r => r.Id == parentId).Select(r => r.Name).SingleAsync(cancellationToken);

        var products = await db.Products.Where(p => p.IsTracked)
            .Include(p => p.Purchases).Include(p => p.Signals).Include(p => p.Substitutes)
            .ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        // Carry each product's curated "also works as" list so the advisor swaps to a stand-in the
        // user has already vouched for before inventing its own.
        var onHand = PantryOnHand.EdibleInStock(products, today)
            .Select(p => new PantryProduct(p.Name, p.Substitutes.Select(s => s.Value).ToList()))
            .OrderBy(p => p.Name)
            .ToList();
        var excluded = await db.ExcludedFoods.Select(f => f.Value).ToListAsync(cancellationToken);

        var input = new RecipeToAdapt(
            recipe.Name,
            recipe.Blurb,
            recipe.Ingredients.Select(i => new AdaptIngredient(i.Name, i.IsMain, i.Quantity)).ToList(),
            recipe.Steps.OrderBy(s => s.Order).Select(s => s.Text).ToList());
        var preference = swap is null ? null : $"Use {swap.ChosenForm} in place of {swap.IngredientName}.";

        RecipeSuggestion? adapted;
        try
        {
            adapted = await advisor.AdaptAsync(input, onHand, excluded, preference, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // the caller cancelled (e.g. circuit gone) — not a failure to report
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Adapting recipe {RecipeId} failed.", recipeId);
            return new AdaptResult(false, "Couldn't reach the assistant to adapt that just now.");
        }
        if (adapted is null || string.IsNullOrWhiteSpace(adapted.Name))
            return new AdaptResult(false, $"Couldn't adapt {recipe.Name} right now.");

        // Guard the chosen swap: if the model ignored it, don't save a mislabeled variant — ask for a retry.
        var adaptedMains = adapted.Ingredients.Where(i => i.IsMain).Select(i => i.Name).ToList();
        if (swap is not null && !IngredientMatcher.IsMentionedIn(swap.ChosenForm, adaptedMains))
        {
            logger.LogWarning("Adapt for recipe {RecipeId} did not honor the chosen swap \"{Form}\".", recipeId, swap.ChosenForm);
            return new AdaptResult(false, $"I couldn't make a {swap.ChosenForm} version this time — give it another try.");
        }

        // Re-adapting to the same result should UPDATE, not duplicate: drop any existing variant of this
        // parent whose main ingredients match (identity by content, not the AI's exact title — robust to
        // slight naming changes). Cascade removes its ingredients/steps.
        var newSig = MainSignature(adapted.Ingredients.Where(i => i.IsMain).Select(i => i.MatchedProduct ?? i.Name));
        var siblings = await db.Recipes.Include(r => r.Ingredients)
            .Where(r => r.ParentRecipeId == parentId).ToListAsync(cancellationToken);
        var stale = siblings
            .Where(s => MainSignature(s.Ingredients.Where(i => i.IsMain).Select(i => i.MatchedProduct ?? i.Name)) == newSig)
            .ToList();
        if (stale.Count > 0) db.Recipes.RemoveRange(stale);

        var variant = new Recipe
        {
            Name = adapted.Name,
            Blurb = adapted.Blurb,
            SavedAt = DateTimeOffset.Now,
            ParentRecipeId = parentId,
            EstimatedCaloriesPerServing = adapted.CaloriesPerServing,
            Ingredients = adapted.Ingredients
                .Select(i => new RecipeIngredient { Name = i.Name, IsMain = i.IsMain, MatchedProduct = i.MatchedProduct, Quantity = i.Quantity })
                .ToList(),
            Steps = adapted.Steps.Select((t, idx) => new RecipeStep { Order = idx + 1, Text = t }).ToList(),
        };
        db.Recipes.Add(variant);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Adapted recipe {RecipeId} into variant {VariantId} (replaced {Removed} duplicate(s)).",
            recipeId, variant.Id, stale.Count);
        return new AdaptResult(true, $"Saved \"{variant.Name}\" — a version of {familyName} using what you have.", variant.Id);
    }

    // A stable, order-independent signature of a recipe's main ingredients (grounded product name when it
    // has one, else the ingredient name) — two variants with the same mains are the same variant.
    private static string MainSignature(IEnumerable<string> mainNames) =>
        string.Join("|", mainNames.Select(n => n.Trim().ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal));
}
