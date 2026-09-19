using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Billing;

namespace ShelfAware.Llm;

/// <summary>
/// LLM-backed <see cref="IIngredientAlternativesAdvisor"/>: asks what a recipe ingredient can be swapped
/// for. Cheap, pinned Haiku, one short call; the caller caches the result on the ingredient. Fails open
/// (returns empty) so the swap cloud never breaks the page.
/// </summary>
public class AnthropicIngredientAlternativesAdvisor : IIngredientAlternativesAdvisor
{
    private const int MaxItems = 6;

    private readonly IChatClient _chat;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicIngredientAlternativesAdvisor> _logger;

    public AnthropicIngredientAlternativesAdvisor(
        IChatClient chat, IOptions<LlmOptions> options, ILogger<AnthropicIngredientAlternativesAdvisor> logger)
    {
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(string ingredientName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ingredientName)) return [];
        await using var action = AiActionScope.Begin(ServiceAction.IngredientAlternatives);
        try
        {
            var prompt =
                $"A cooking app lets a user swap a recipe ingredient. Ingredient: \"{ingredientName.Trim()}\".\n\n" +
                "List the realistic swaps a home cook could use in its place — interchangeable cuts or forms " +
                "of the same food (chicken breast → chicken thighs, chicken tenderloins, chicken cutlets) or " +
                "close stand-ins (chicken → turkey). NOT wildly different foods. Do NOT repeat the ingredient " +
                "itself. Reply as a short comma-separated list of lowercase phrases (2 to 6). If there are no " +
                "meaningful swaps (a spice, a very specific one-off), reply with only: NONE";

            var options = new ChatOptions { ModelId = _options.ExtractionModel, MaxOutputTokens = 128 };
            var response = await _chat.GetResponseAsync(prompt, options, cancellationToken);

            var reply = response.Text.Trim();
            if (!ProviderReply.IsAnAnswer(reply)) return [];
            // ⚠️ Settled BEFORE the sentinel, not after the parse. "NONE" is the model's considered
            // answer to a question the household asked, and an answer is paid for; only a provider
            // that said nothing at all is refunded. Everything below this line is us INTERPRETING
            // a reply we were given.
            action.Answered();
            if (ProviderReply.IsNothingFound(reply)) return [];
            return Parse(reply, ingredientName);
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
            _logger.LogWarning(ex, "Ingredient-alternatives suggestion failed for \"{Ingredient}\"; returning none.", ingredientName.Trim());
            return [];
        }
    }

    private static IReadOnlyList<string> Parse(string reply, string ingredientName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in reply.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = raw.Trim().TrimEnd('.').Trim();
            if (value.Length == 0 || string.Equals(value, ingredientName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(value)) result.Add(value.ToLowerInvariant());
            if (result.Count >= MaxItems) break;
        }
        return result;
    }
}
