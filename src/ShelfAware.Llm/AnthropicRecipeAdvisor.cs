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
                    "main": { "type": "boolean" },
                    "matched_product": { "type": ["string", "null"] }
                  },
                  "required": ["name", "main", "matched_product"],
                  "additionalProperties": false
                }
              },
              "steps": {
                "type": "array",
                "items": { "type": "string" },
                "description": "Ordered cooking method, one short instruction per element."
              }
            },
            "required": ["name", "blurb", "ingredients", "steps"],
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
                        i.TryGetProperty("matched_product", out var mp) && mp.ValueKind == JsonValueKind.String ? mp.GetString() : null));
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
            recipes.Add(new RecipeSuggestion(
                r.GetProperty("name").GetString() ?? "",
                r.TryGetProperty("blurb", out var b) ? b.GetString() ?? "" : "",
                ingredients,
                steps));
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
