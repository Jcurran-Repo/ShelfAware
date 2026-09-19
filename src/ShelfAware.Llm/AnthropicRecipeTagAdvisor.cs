using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Billing;

namespace ShelfAware.Llm;

/// <summary>
/// LLM-backed <see cref="IRecipeTagAdvisor"/>: asks the model for a few descriptive tags (meal, cuisine,
/// diet, dish type) for a recipe, reusing the household's known tags so it leans on the existing
/// vocabulary rather than coining near-duplicates. Cheap, pinned Haiku, one short call. Fails open
/// (returns empty) so a flaky API never blocks saving, importing, or viewing a recipe.
/// </summary>
public class AnthropicRecipeTagAdvisor : IRecipeTagAdvisor
{
    private const int MaxItems = 5;

    private readonly IChatClient _chat;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicRecipeTagAdvisor> _logger;

    public AnthropicRecipeTagAdvisor(
        IChatClient chat, IOptions<LlmOptions> options, ILogger<AnthropicRecipeTagAdvisor> logger)
    {
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(
        string recipeName,
        IReadOnlyList<string> ingredientNames,
        IReadOnlyList<string> knownTags,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipeName)) return [];
        await using var action = AiActionScope.Begin(ServiceAction.TagSuggest);
        try
        {
            var ingredients = ingredientNames.Count > 0 ? string.Join(", ", ingredientNames) : "(not listed)";
            var known = knownTags.Count > 0
                ? "Prefer these existing tags where they fit, so the vocabulary stays tidy: "
                  + string.Join(", ", knownTags) + ".\n"
                : "";

            var prompt =
                $"A cooking app browses recipes by tag. Recipe: \"{recipeName.Trim()}\".\n" +
                $"Ingredients: {ingredients}.\n\n" +
                "Give 2 to 4 short descriptive tags for browsing — think meal (Breakfast/Dinner/Dessert), " +
                "cuisine (Italian/Mexican/Asian), diet (Vegetarian/Vegan/Gluten-Free), or dish type " +
                "(Soup/Salad/Pasta/One-Pot). Only tags a person would actually filter by; no ingredient " +
                "names, no made-up words.\n" +
                known +
                "Reply as a short comma-separated list, each tag in Title Case (2 to 4 items). If nothing " +
                "fits, reply with only: NONE";

            var options = new ChatOptions { ModelId = _options.ExtractionModel, MaxOutputTokens = 64 };
            var response = await _chat.GetResponseAsync(prompt, options, cancellationToken);

            var reply = response.Text.Trim();
            if (!ProviderReply.IsAnAnswer(reply)) return [];
            // ⚠️ Settled BEFORE the sentinel, not after the parse. "NONE" is the model's considered
            // answer to a question the household asked, and an answer is paid for; only a provider
            // that said nothing at all is refunded. Everything below this line is us INTERPRETING
            // a reply we were given.
            action.Answered();
            if (ProviderReply.IsNothingFound(reply)) return [];
            return Parse(reply);
        }
        // ⚠️ WHOSE cancellation, and the unconditional version of this line was wrong in every
        // reachable case. A caller that cancelled is not this advisor's to absorb and is rethrown. A
        // provider TIMEOUT arrives as the same type and is a degraded provider — the case the catch
        // below exists for, whose log line is the operator's only signal that the dedup is silently
        // failing open. Not one call site passes a token today (Upload.razor, ProductDetail.razor,
        // Recipes.razor all take the CancellationToken.None default), so an unconditional rethrow
        // reclassifies 100% of real cancellations as caller intent — and since those sites are a
        // try/finally with no catch and the app has no ErrorBoundary, it escapes an @onclick handler
        // and tears down the Blazor circuit, losing an in-progress receipt review. The token decides.
        //
        // Billing is the same either way: nothing above has settled, so the act refunds.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recipe tag suggestion failed for \"{Recipe}\"; returning none.", recipeName.Trim());
            return [];
        }
    }

    // Split the comma list, trim, drop blanks/dupes, cap the count. Canonicalization against the
    // household's vocabulary happens in RecipeTagVocabulary at apply time — this just cleans the reply.
    private static IReadOnlyList<string> Parse(string reply)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in reply.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = raw.Trim().TrimEnd('.').Trim();
            // Drop blanks AND a stray "NONE" token — a malformed "NONE, Dinner" reply must not store a
            // literal NONE tag beside the real ones (the whole-reply sentinel only catches a lone "NONE").
            if (value.Length == 0 || value.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(value)) result.Add(value);
            if (result.Count >= MaxItems) break;
        }
        return result;
    }
}
