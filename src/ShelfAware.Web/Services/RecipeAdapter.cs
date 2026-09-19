using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;
using ShelfAware.Web.Undo;

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
    IHouseholdDbFactory dbFactory, IRecipeAdvisor advisor, IAppSettings settings,
    IActivityLog activityLog, ILogger<RecipeAdapter> logger) : IRecipeAdapter
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
        // user has already vouched for before inventing its own. Expiration tracking rides along:
        // when the household turned it on, an expired ingredient is NOT on hand to adapt toward.
        var trackExpirations = await settings.GetTrackExpirationDatesAsync(cancellationToken);
        var onHand = PantryOnHand.EdibleInStock(products, today, trackExpirations)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } // whose cancellation: see ProviderCancellationSiteTests
        catch (Exception ex)
        {
            logger.LogError(ex, "Adapting recipe {RecipeId} failed.", recipeId);
            return new AdaptResult(false, "Couldn't reach the assistant to adapt that just now.");
        }
        // One predicate with the settlement that pays for it — see RecipeReply.Landed.
        if (!adapted.Landed())
            return new AdaptResult(false, $"Couldn't adapt {recipe.Name} right now.");

        // ⚠️ A swap the model ignored is LABELLED, not discarded. This used to return failure and invite a
        // retry — "I couldn't make a {form} version this time — give it another try" — on an act that had
        // already been charged: paid work thrown away, an invitation to pay again, and nobody able to see
        // what the model actually made. Asking is what is paid for (§4.w), the household asked, so the
        // variant is saved and the charge stands; what changes is that we say what it is. See
        // AdaptResult.SwapIgnored.
        var adaptedMains = adapted.Ingredients.Where(i => i.IsMain).Select(i => i.Name).ToList();
        var swapIgnored = swap is not null && !IngredientMatcher.IsMentionedIn(swap.ChosenForm, adaptedMains)
            ? swap.ChosenForm
            : null;
        if (swapIgnored is not null)
            logger.LogWarning("Adapt for recipe {RecipeId} did not honor the chosen swap \"{Form}\"; saving it labelled.",
                recipeId, swapIgnored);

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
            // ⚠️ The label travels WITH the row, not only in the message that announced it. A message is
            // read once; the variant sits in the cookbook indefinitely, and a household that asked for a
            // chickpea version and finds a beef one months later has no way to know it was the model that
            // ignored them rather than themselves misremembering.
            Blurb = swapIgnored is null
                ? adapted.Blurb
                : $"{adapted.Blurb} (You asked for a {swapIgnored} version — this one doesn't use it.)".TrimStart(),
            SavedAt = DateTimeOffset.Now,
            ParentRecipeId = parentId,
            EstimatedCaloriesPerServing = adapted.CaloriesPerServing,
            Servings = adapted.Servings,
            Ingredients = adapted.Ingredients
                .Select(i => new RecipeIngredient { Name = i.Name, IsMain = i.IsMain, MatchedProduct = i.MatchedProduct, Quantity = i.Quantity })
                .ToList(),
            Steps = adapted.Steps.Select((t, idx) => new RecipeStep { Order = idx + 1, Text = t }).ToList(),
        };
        db.Recipes.Add(variant);
        // Undoable, only here past the guards (a failed adapt logs nothing; a dishonoured swap DOES, because
        // it saved a real variant the household will want to undo). The entry keys
        // on the variant's generated id (so undo can delete it), so the variant + the stale-variant removals
        // save inside a transaction to assign it, then the entry is staged and saved — one commit.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken); // assigns variant.Id
        activityLog.Record(db, ActivityKind.RecipeAdapted, new RecipeAdaptedPayload(variant.Id, familyName, variant.Name));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        logger.LogInformation("Adapted recipe {RecipeId} into variant {VariantId} (replaced {Removed} duplicate(s)).",
            recipeId, variant.Id, stale.Count);
        return swapIgnored is null
            ? new AdaptResult(true, $"Saved \"{variant.Name}\" — a version of {familyName} using what you have.", variant.Id)
            : new AdaptResult(true,
                $"Saved \"{variant.Name}\", but it doesn't use {swapIgnored} — the assistant ignored the swap you picked.",
                variant.Id, swapIgnored);
    }

    // A stable, order-independent signature of a recipe's main ingredients (grounded product name when it
    // has one, else the ingredient name) — two variants with the same mains are the same variant.
    private static string MainSignature(IEnumerable<string> mainNames) =>
        string.Join("|", mainNames.Select(n => n.Trim().ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal));
}
