using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Llm;

/// <summary>
/// <see cref="IRecipeAdvisor"/> over the Anthropic Messages API with structured outputs. Same pinned
/// model + direct-SDK pattern as the extractor and chat.
/// </summary>
public class AnthropicRecipeAdvisor : IRecipeAdvisor
{
    private static readonly string SystemPrompt = ReadEmbedded("Prompts.recipe-suggest-system.txt");
    private static readonly string AdaptSystemPrompt = ReadEmbedded("Prompts.recipe-adapt-system.txt");

    private const string OutputSchemaJson = """
    {
      "type": "object",
      "properties": {
        "recipes": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name":  { "type": "string" },
              "blurb": { "type": "string" },
              "ingredients": {
                "type": "array",
                "items": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string" },
                    "quantity": {
                      "type": ["string", "null"],
                      "description": "Amount as a recipe would write it, e.g. \"2 lbs\", \"3 cloves\", \"1 (14 oz) can\", \"to taste\". null only if truly not applicable."
                    },
                    "main": { "type": "boolean" },
                    "matched_product": { "type": ["string", "null"] }
                  },
                  "required": ["name", "quantity", "main", "matched_product"],
                  "additionalProperties": false
                }
              },
              "steps": {
                "type": "array",
                "items": { "type": "string" },
                "description": "Ordered cooking method, one short instruction per element."
              },
              "calories_per_serving": {
                "type": ["integer", "null"],
                "description": "Rough estimated calories per serving (ballpark for planning, not precise nutrition). null only if truly unable to estimate."
              }
            },
            "required": ["name", "blurb", "ingredients", "steps", "calories_per_serving"],
            "additionalProperties": false
          }
        }
      },
      "required": ["recipes"],
      "additionalProperties": false
    }
    """;

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
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                JsonSerializer.Deserialize<JsonElement>(OutputSchemaJson),
                schemaName: "recipe_suggestions"),
        };

        var response = await _chat.GetResponseAsync(messages, options, cancellationToken);
        var suggestions = Parse(response.Text);
        _logger.LogInformation("Recipe advisor returned {Count} suggestion(s) for {OnHand} on-hand item(s).", suggestions.Count, onHand.Count);
        return suggestions;
    }

    public async Task<RecipeSuggestion?> AdaptAsync(
        RecipeToAdapt recipe, IReadOnlyList<PantryProduct> onHand, IReadOnlyList<string> excludedFoods,
        string? preference = null, CancellationToken cancellationToken = default)
    {
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
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                JsonSerializer.Deserialize<JsonElement>(OutputSchemaJson), schemaName: "recipe_adaptation"),
        };

        var response = await _chat.GetResponseAsync(messages, options, cancellationToken);
        var adapted = Parse(response.Text).FirstOrDefault();
        _logger.LogInformation("Recipe advisor adapted \"{Name}\" (produced result: {HasResult}).", recipe.Name, adapted is not null);
        return adapted;
    }

    private static List<RecipeSuggestion> Parse(string json)
    {
        var recipes = new List<RecipeSuggestion>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("recipes", out var arr)) return recipes;
        foreach (var r in arr.EnumerateArray())
        {
            var ingredients = new List<SuggestedIngredient>();
            if (r.TryGetProperty("ingredients", out var ing))
            {
                foreach (var i in ing.EnumerateArray())
                {
                    ingredients.Add(new SuggestedIngredient(
                        i.GetProperty("name").GetString() ?? "",
                        i.TryGetProperty("main", out var m) && m.ValueKind == JsonValueKind.True,
                        i.TryGetProperty("matched_product", out var mp) && mp.ValueKind == JsonValueKind.String ? mp.GetString() : null,
                        i.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null));
                }
            }
            var steps = new List<string>();
            if (r.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Array)
            {
                steps.AddRange(st.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim())
                    .Where(s => s.Length > 0));
            }
            int? calories = r.TryGetProperty("calories_per_serving", out var cal) && cal.ValueKind == JsonValueKind.Number
                ? cal.GetInt32()
                : null;
            recipes.Add(new RecipeSuggestion(
                r.GetProperty("name").GetString() ?? "",
                r.TryGetProperty("blurb", out var b) ? b.GetString() ?? "" : "",
                ingredients,
                steps,
                calories));
        }
        return recipes;
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
