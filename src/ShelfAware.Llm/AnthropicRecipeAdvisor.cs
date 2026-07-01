using System.Reflection;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
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
                    "have": { "type": "boolean" }
                  },
                  "required": ["name", "main", "have"],
                  "additionalProperties": false
                }
              }
            },
            "required": ["name", "blurb", "ingredients"],
            "additionalProperties": false
          }
        }
      },
      "required": ["recipes"],
      "additionalProperties": false
    }
    """;

    private readonly AnthropicClient _client;
    private readonly LlmOptions _options;

    public AnthropicRecipeAdvisor(IOptions<LlmOptions> options)
    {
        _options = options.Value;
        _client = new AnthropicClient { ApiKey = _options.ApiKey };
    }

    public async Task<IReadOnlyList<RecipeSuggestion>> SuggestAsync(
        string request, IReadOnlyList<string> onHand, IReadOnlyList<string> excludedFoods,
        CancellationToken cancellationToken = default)
    {
        var content =
            $"Request: {request}\n\n" +
            "Likely on hand:\n" + (onHand.Count > 0 ? "- " + string.Join("\n- ", onHand) : "(nothing recorded)") + "\n\n" +
            "Will NOT eat (exclude entirely):\n" + (excludedFoods.Count > 0 ? "- " + string.Join("\n- ", excludedFoods) : "(none)");

        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = _options.ChatModel,
            MaxTokens = 2048,
            System = SystemPrompt,
            OutputConfig = new OutputConfig
            {
                Format = new JsonOutputFormat
                {
                    Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(OutputSchemaJson)!,
                },
            },
            Messages = [new() { Role = Role.User, Content = content }],
        }, cancellationToken: cancellationToken);

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return Parse(json);
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
                        i.TryGetProperty("have", out var h) && h.ValueKind == JsonValueKind.True));
                }
            }
            recipes.Add(new RecipeSuggestion(
                r.GetProperty("name").GetString() ?? "",
                r.TryGetProperty("blurb", out var b) ? b.GetString() ?? "" : "",
                ingredients));
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
