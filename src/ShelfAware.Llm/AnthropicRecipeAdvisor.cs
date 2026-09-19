using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Billing;

namespace ShelfAware.Llm;

/// <summary>
/// <see cref="IRecipeAdvisor"/> over the Anthropic Messages API with structured outputs. Same pinned
/// model + direct-SDK pattern as the extractor and chat. The recipe JSON shape and its parse live in
/// <see cref="RecipeJson"/>, shared with the meal-plan generator so they can't drift.
/// </summary>
public class AnthropicRecipeAdvisor : IRecipeAdvisor
{
    private static readonly string SystemPrompt = ReadEmbedded("Prompts.recipe-suggest-system.txt");
    private static readonly string AdaptSystemPrompt = ReadEmbedded("Prompts.recipe-adapt-system.txt");

    private readonly IChatClient _chat;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicRecipeAdvisor> _logger;

    public AnthropicRecipeAdvisor(IChatClient chat, IOptions<LlmOptions> options, ILogger<AnthropicRecipeAdvisor> logger)
    {
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RecipeSuggestion>> SuggestAsync(
        string request, IReadOnlyList<string> onHand, IReadOnlyList<string> excludedFoods,
        CancellationToken cancellationToken = default)
    {
        await using var action = AiActionScope.Begin(ServiceAction.RecipeSuggest);
        var content =
            $"Request: {request}\n\n" +
            "Likely on hand:\n" + (onHand.Count > 0 ? "- " + string.Join("\n- ", onHand) : "(nothing recorded)") + "\n\n" +
            "Will NOT eat (exclude entirely):\n" + (excludedFoods.Count > 0 ? "- " + string.Join("\n- ", excludedFoods) : "(none)");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, content),
        };
        var options = new ChatOptions
        {
            ModelId = _options.ChatModel,
            MaxOutputTokens = 4096, // steps add length beyond name/blurb/ingredients
            ResponseFormat = ChatResponseFormat.ForJsonSchema(RecipeJson.Schema(), schemaName: "recipe_suggestions"),
        };

        var response = await _chat.GetResponseAsync(messages, options, cancellationToken);
        var suggestions = RecipeJson.Parse(response.Text);
        _logger.LogInformation("Recipe advisor returned {Count} suggestion(s) for {OnHand} on-hand item(s).", suggestions.Count, onHand.Count);
        action.Answered(); // "nothing I can make from that" is an answer; an unreadable reply throws above
        return suggestions;
    }

    public async Task<RecipeSuggestion?> AdaptAsync(
        RecipeToAdapt recipe, IReadOnlyList<PantryProduct> onHand, IReadOnlyList<string> excludedFoods,
        string? preference = null, CancellationToken cancellationToken = default)
    {
        await using var action = AiActionScope.Begin(ServiceAction.RecipeAdapt);
        var ingredients = string.Join("\n", recipe.Ingredients.Select(i =>
            $"- {(string.IsNullOrWhiteSpace(i.Quantity) ? "" : i.Quantity + " ")}{i.Name}{(i.IsMain ? "" : " (seasoning)")}"));
        var steps = recipe.Steps.Count > 0
            ? string.Join("\n", recipe.Steps.Select((s, i) => $"{i + 1}. {s}"))
            : "(none)";
        // Each on-hand line carries the user's curated "also works as" list (rule 9) so the model swaps
        // to a product the user has already declared a valid stand-in before inventing its own.
        var onHandLines = onHand.Select(p => p.AlsoWorksAs.Count > 0
            ? $"{p.Name} (also works as: {string.Join(", ", p.AlsoWorksAs)})"
            : p.Name).ToList();
        var content =
            (string.IsNullOrWhiteSpace(preference)
                ? ""
                : $"USER'S CHOSEN SWAP (MANDATORY — build the recipe around this exact form even if it isn't on hand; see rule 8): {preference}\n\n") +
            $"Original recipe: {recipe.Name}\n" +
            (string.IsNullOrWhiteSpace(recipe.Blurb) ? "" : $"Blurb: {recipe.Blurb}\n") +
            $"Ingredients:\n{ingredients}\n\nSteps:\n{steps}\n\n" +
            "Likely on hand:\n" + (onHandLines.Count > 0 ? "- " + string.Join("\n- ", onHandLines) : "(nothing recorded)") + "\n\n" +
            "Will NOT eat (exclude entirely):\n" + (excludedFoods.Count > 0 ? "- " + string.Join("\n- ", excludedFoods) : "(none)");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AdaptSystemPrompt),
            new(ChatRole.User, content),
        };
        var options = new ChatOptions
        {
            ModelId = _options.ChatModel,
            MaxOutputTokens = 4096,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(RecipeJson.Schema(), schemaName: "recipe_adaptation"),
        };

        var response = await _chat.GetResponseAsync(messages, options, cancellationToken);
        var adapted = RecipeJson.Parse(response.Text).FirstOrDefault();
        _logger.LogInformation("Recipe advisor adapted \"{Name}\" (produced result: {HasResult}).", recipe.Name, adapted is not null);
        // ⚠️ Settled on a NAMED adaptation, not on the call returning. A reply that parses to nothing,
        // or to a variant with no name, is one RecipeAdapter turns into "Couldn't adapt {recipe} right
        // now." — and it used to charge a full credit for it, then invite the household to press the
        // button again and charge again. Charging for a turn the household demonstrably did not receive
        // is the thing docs/subscription-plan.md §4.w exists to stop; the old comment here called it
        // "including this can't be adapted to what you have", which is a different and honest answer the
        // model never actually gave.
        //
        // ⚠️ One half of this is NOT fixed here and is in docs/backlog.md: RecipeAdapter also rejects
        // an adaptation that ignored the chosen swap, with the same retry invitation, and that one stays
        // charged. This method cannot see that check, and moving the scope out to RecipeAdapter so the
        // one place that knows whether the act delivered is the one place that settles it is a design
        // change rather than a fix — it is Jordan's call, per CLAUDE.md's co-creation rule.
        if (adapted is null || string.IsNullOrWhiteSpace(adapted.Name)) return adapted;
        action.Answered();
        return adapted;
    }

    private static string ReadEmbedded(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"ShelfAware.Llm.{suffix}")
            ?? throw new InvalidOperationException($"Embedded resource {suffix} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
