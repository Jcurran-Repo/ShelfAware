using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Llm;

/// <summary>
/// LLM-backed <see cref="IProductSubstituteAdvisor"/>: asks the model what recipe ingredients a product can
/// stand in for. Cheap, pinned Haiku, one short call. Fails open (returns empty) so a flaky API never
/// blocks the product page. The user curates the result — this is only a first pass.
/// </summary>
public class AnthropicProductSubstituteAdvisor : IProductSubstituteAdvisor
{
    private const int MaxItems = 8;

    private readonly IChatClient _chat;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicProductSubstituteAdvisor> _logger;

    public AnthropicProductSubstituteAdvisor(
        IChatClient chat, IOptions<LlmOptions> options, ILogger<AnthropicProductSubstituteAdvisor> logger)
    {
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(
        string productName, string category, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productName)) return [];
        try
        {
            var prompt =
                $"A cooking app tracks a pantry. Product: \"{productName.Trim()}\" (aisle: {category}).\n\n" +
                "List the recipe ingredients this product can be used IN PLACE OF — a cook could reach for " +
                "this when a recipe calls for that ingredient, with the same role and similar cooking. Only " +
                "CLOSE, realistic swaps: interchangeable cuts or forms of the same food (chicken breast " +
                "tenderloins can replace chicken breast or chicken cutlet), NOT a wildly different form (a " +
                "whole roaster chicken is NOT a chicken breast) and NOT a different food. Do not include the " +
                "product's own name. Reply as a short comma-separated list of lowercase phrases (2 to 6 " +
                "items). If there are no meaningful swaps (a seasoning, a one-off, a staple), reply with only: NONE";

            var options = new ChatOptions { ModelId = _options.ExtractionModel, MaxOutputTokens = 128 };
            var response = await _chat.GetResponseAsync(prompt, options, cancellationToken);

            var reply = response.Text.Trim();
            if (reply.Length == 0 || reply.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return [];
            return Parse(reply, productName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Substitute suggestion failed for \"{Product}\"; returning none.", productName.Trim());
            return [];
        }
    }

    // Split the comma list, trim, drop blanks/the product's own name/dupes, cap the count.
    private static IReadOnlyList<string> Parse(string reply, string productName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in reply.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = raw.Trim().TrimEnd('.').Trim();
            if (value.Length == 0 || string.Equals(value, productName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(value)) result.Add(value.ToLowerInvariant());
            if (result.Count >= MaxItems) break;
        }
        return result;
    }
}
