using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Recipes;

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
            if (reply.Length == 0 || reply.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return [];
            return Parse(reply, ingredientName);
        }
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
