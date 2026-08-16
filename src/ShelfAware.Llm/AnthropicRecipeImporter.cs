using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Llm;

/// <summary>
/// Extracts a structured recipe from a photo (vision) or pasted text, for the cookbook's import flow.
/// Same machinery as the receipt extractor and shelf-census reader: a strict JSON schema enforced
/// server-side, a validate-and-retry-once loop, and a defensive manual parse. It reports "no recipe
/// found" (the schema's <c>found</c> flag) rather than inventing one — the anti-hallucination floor,
/// since a photo or a paste can contain no recipe at all. The result is always REVIEWED before saving.
/// </summary>
public class AnthropicRecipeImporter : IRecipeImporter
{
    // Strict-mode conventions (as in the receipt/census schemas): every property required, nullables as
    // type unions, additionalProperties:false, no numeric range constraints.
    private const string OutputSchemaJson = """
    {
      "type": "object",
      "properties": {
        "found": { "type": "boolean" },
        "name": { "type": ["string", "null"] },
        "blurb": { "type": ["string", "null"] },
        "calories_per_serving": { "type": ["integer", "null"] },
        "ingredients": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "quantity": { "type": ["string", "null"] },
              "is_main": { "type": "boolean" }
            },
            "required": ["name", "quantity", "is_main"],
            "additionalProperties": false
          }
        },
        "steps": { "type": "array", "items": { "type": "string" } },
        "tags": { "type": "array", "items": { "type": "string" } }
      },
      "required": ["found", "name", "blurb", "calories_per_serving", "ingredients", "steps", "tags"],
      "additionalProperties": false
    }
    """;

    private static readonly string ImagePrompt = ReadEmbedded("Prompts.recipe-import-image-system.txt");
    private static readonly string TextPrompt = ReadEmbedded("Prompts.recipe-import-text-system.txt");

    private readonly IChatClient _chat;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicRecipeImporter> _logger;

    public AnthropicRecipeImporter(
        IChatClient chat, IOptions<LlmOptions> options, ILogger<AnthropicRecipeImporter> logger)
    {
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    public Task<RecipeImportResult> ImportFromImageAsync(RecipePhoto image, CancellationToken cancellationToken = default)
    {
        var content = new List<AIContent>
        {
            new DataContent(image.Data, image.MediaType),
            new TextContent("Extract the recipe in this image."),
        };
        return ExtractAsync(ImagePrompt, content, _options.ExtractionModel, cancellationToken);
    }

    public Task<RecipeImportResult> ImportFromTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(RecipeImportResult.Fail("Paste a recipe first."));
        var content = new List<AIContent> { new TextContent($"Structure this recipe text:\n\n{text}") };
        return ExtractAsync(TextPrompt, content, _options.ChatModel, cancellationToken);
    }

    private async Task<RecipeImportResult> ExtractAsync(
        string systemPrompt, List<AIContent> userContent, string model, CancellationToken cancellationToken)
    {
        var options = new ChatOptions
        {
            ModelId = model,
            MaxOutputTokens = 4096,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                JsonSerializer.Deserialize<JsonElement>(OutputSchemaJson), schemaName: "recipe_import"),
        };

        var rawJson = "";
        string? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userContent),
            };
            if (attempt > 0)
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, rawJson));
                messages.Add(new ChatMessage(ChatRole.User,
                    $"Your previous output failed validation: {lastError}. Output corrected JSON matching the schema."));
            }

            ChatResponse response;
            try
            {
                response = await _chat.GetResponseAsync(messages, options, cancellationToken);
            }
            catch (OperationCanceledException) { throw; } // caller cancelled — not a failure
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recipe import call failed.");
                return RecipeImportResult.Fail("Couldn't reach the AI just now — please try again.");
            }

            rawJson = response.Text;
            try { return Parse(rawJson); }
            catch (Exception ex) { lastError = ex.Message; } // any invalid shape is retryable
        }

        _logger.LogWarning("Recipe import couldn't be parsed after a retry: {Error}", lastError);
        return RecipeImportResult.Fail("Couldn't read a recipe from that — try a clearer photo or paste the text.");
    }

    private static RecipeImportResult Parse(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var name = GetNullableString(root, "name");
        // The model says so itself when there's nothing to extract — the anti-hallucination floor.
        if (!root.GetProperty("found").GetBoolean() || string.IsNullOrWhiteSpace(name))
            return RecipeImportResult.Fail("No recipe found — try a clearer photo, or paste the recipe text.");

        var ingredients = new List<ImportedIngredient>();
        foreach (var item in root.GetProperty("ingredients").EnumerateArray())
        {
            var ingredientName = item.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(ingredientName)) continue;
            ingredients.Add(new ImportedIngredient(
                ingredientName.Trim(), GetNullableString(item, "quantity"), item.GetProperty("is_main").GetBoolean()));
        }

        var steps = root.GetProperty("steps").EnumerateArray()
            .Select(s => s.GetString()?.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
        var tags = root.GetProperty("tags").EnumerateArray()
            .Select(t => t.GetString()?.Trim()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();

        int? calories = root.TryGetProperty("calories_per_serving", out var cal) && cal.ValueKind == JsonValueKind.Number
            ? cal.GetInt32()
            : null;

        return RecipeImportResult.Ok(new ImportedRecipe(
            name.Trim(), GetNullableString(root, "blurb"), calories, ingredients, steps, tags));
    }

    private static string? GetNullableString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static string ReadEmbedded(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"ShelfAware.Llm.{suffix}")
            ?? throw new InvalidOperationException($"Embedded resource {suffix} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
