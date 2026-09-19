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
/// <param name="SwapIgnored">The form the household picked that the model did not use, when it saved a
/// variant anyway; null when there was no swap or the swap was honoured.
///
/// <para>⚠️ The variant is still SAVED and the act is still CHARGED in this case, which is a deliberate
/// call (Jordan, 2026-09-19) and the opposite of what this path used to do. It used to discard the
/// adaptation and tell the household "I couldn't make a {form} version this time — give it another try",
/// having already charged for it: paid work thrown away, an invitation to pay again, and no way for
/// anyone to see what the model actually produced. The rule is that asking is what is paid for — the
/// household asked for the swap, so §4.w charges — and a turn that produced a real adaptation of the
/// wrong shape is not a failure to refund, it is a result to hand over with an honest label.</para>
///
/// <para>So the caller's job when this is set is to give them the recipe, say plainly that it does not
/// use the form they picked, and offer to file a bug report — never to hide it or to imply the save was
/// clean. The note also rides on the saved variant's blurb, so the row is still self-describing weeks
/// later when the message that came with it is long gone.</para></param>
public record AdaptResult(bool Success, string Message, int? VariantId = null, string? SwapIgnored = null);
