using System.Text.Json;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Llm;

/// <summary>The ONE definition of the recipe structured-output shape and its defensive parse — shared by
/// the recipe advisor (suggest + adapt) and the meal-plan generator, so they can't drift on the schema or
/// on how a model response is read back. A "recipes" array of {name, blurb, ingredients[], steps[],
/// calories_per_serving}, parsed into <see cref="RecipeSuggestion"/>.</summary>
internal static class RecipeJson
{
    public const string SchemaJson = """
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

    /// <summary>The schema as a <see cref="JsonElement"/> for <c>ChatResponseFormat.ForJsonSchema</c>.</summary>
    public static JsonElement Schema() => JsonSerializer.Deserialize<JsonElement>(SchemaJson);

    /// <summary>Read a model response into recipes — tolerant of a missing "recipes" property, absent
    /// fields, and blank steps (structured outputs make malformed responses near-impossible, but the parse
    /// must never assume it).</summary>
    public static List<RecipeSuggestion> Parse(string json)
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
}
