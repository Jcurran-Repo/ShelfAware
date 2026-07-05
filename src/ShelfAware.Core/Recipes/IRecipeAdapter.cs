namespace ShelfAware.Core.Recipes;

/// <summary>
/// Adapts a saved recipe to what's on hand and saves the result as a variant of the original. Orchestration
/// port (defined in Core, implemented in Web) so BOTH the recipe page's "Adapt" button and the chat/voice
/// <c>adapt_recipe</c> tool share one path — the Web impl owns the DB + the on-hand computation, the LLM
/// rewrite rides on <see cref="IRecipeAdvisor.AdaptAsync"/>. On-demand only (never on load) to save calls.
/// </summary>
public interface IRecipeAdapter
{
    /// <param name="swap">An explicit ingredient swap the user picked from the bubble cloud, or null to
    /// adapt everything to what's on hand.</param>
    Task<AdaptResult> AdaptToOnHandAsync(int recipeId, IngredientSwap? swap = null, CancellationToken cancellationToken = default);
}

/// <summary>A specific swap chosen for one ingredient — use <see cref="ChosenForm"/> ("chicken thighs")
/// in place of <see cref="IngredientName"/> ("chicken breast").</summary>
public record IngredientSwap(string IngredientName, string ChosenForm);

/// <param name="Success">Whether a variant was created.</param>
/// <param name="Message">A one-line, user-facing summary (spoken/shown).</param>
/// <param name="VariantId">The new variant's id when created, else null.</param>
public record AdaptResult(bool Success, string Message, int? VariantId = null);
