using System.Reflection;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Llm;

/// <summary>
/// <see cref="IMealPlanGenerator"/> over the Anthropic Messages API with structured outputs. Same pinned
/// model + direct-SDK pattern as the recipe advisor, and it shares the recipe JSON shape + parse
/// (<see cref="RecipeJson"/>) so a planned meal reads back exactly like a suggestion. One call per batch of
/// slots; the service loops a long horizon a week at a time.
/// </summary>
public class AnthropicMealPlanGenerator : IMealPlanGenerator
{
    private static readonly string SystemPrompt = ReadEmbedded("Prompts.meal-plan-system.txt");

    private readonly IChatClient _chat;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicMealPlanGenerator> _logger;

    public AnthropicMealPlanGenerator(IChatClient chat, IOptions<LlmOptions> options, ILogger<AnthropicMealPlanGenerator> logger)
    {
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RecipeSuggestion>> GenerateAsync(
        MealPlanBatch batch, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, BuildContent(batch)),
        };
        var options = new ChatOptions
        {
            ModelId = _options.ChatModel,
            MaxOutputTokens = 8192, // a batch of full recipes (name/blurb/ingredients/steps) per call
            ResponseFormat = ChatResponseFormat.ForJsonSchema(RecipeJson.Schema(), schemaName: "meal_plan"),
        };

        // Validate-and-retry-once (the extractor's discipline): a long structured response can come back
        // TRUNCATED (a string cut off mid-JSON) or empty, which would otherwise throw and take the whole
        // plan down. Retry once; if it still fails, return NOTHING for this batch — the service tolerates a
        // short batch rather than crashing. Never throws except on cancellation.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var response = await _chat.GetResponseAsync(messages, options, cancellationToken);
                var meals = RecipeJson.Parse(response.Text);
                if (meals.Count > 0)
                {
                    _logger.LogInformation("Meal-plan generator returned {Count} meal(s) for {Slots} slot(s).",
                        meals.Count, batch.Slots.Count);
                    return meals;
                }
                _logger.LogWarning("Meal-plan batch returned no meals (attempt {Attempt} of 2).", attempt);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Meal-plan batch failed (attempt {Attempt} of 2) — {Action}.",
                    attempt, attempt < 2 ? "retrying" : "giving up on this batch");
            }
        }
        return [];
    }

    private static string BuildContent(MealPlanBatch batch)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Slots to fill (produce ONE recipe for each, in this order):");
        for (var i = 0; i < batch.Slots.Count; i++)
        {
            var slot = batch.Slots[i];
            var cal = slot.Calories is { } c ? $", ~{c} cal" : "";
            sb.AppendLine($"{i + 1}. Day {slot.Day + 1} {slot.Slot.ToString().ToLowerInvariant()} ({slot.Effort.ToString().ToLowerInvariant()} effort{cal})");
        }
        sb.AppendLine();

        sb.AppendLine("Preferences:");
        if (batch.Settings.Appliances.Count > 0)
            sb.AppendLine($"- Extra appliances (beyond oven + stovetop): {string.Join(", ", batch.Settings.Appliances)}");
        if (batch.Settings.ProteinGramsPerDay is { } protein)
            sb.AppendLine($"- Daily protein target: ~{protein} g");
        if (batch.Settings.CarbGramsPerDay is { } carbs)
            sb.AppendLine($"- Daily carb target: ~{carbs} g");
        if (batch.Settings.FoodGroups.Count > 0)
            sb.AppendLine($"- Food groups to cover / balance across the plan: {string.Join(", ", batch.Settings.FoodGroups)}");
        if (batch.Settings.PreferLeftovers)
            sb.AppendLine("- Prefer leftovers: yes — cook once, eat twice (see rule 13)");
        sb.AppendLine($"- Invent: {(batch.Settings.Invent ? "yes" : "no")}");
        sb.AppendLine();

        AppendSection(sb, "Likely have on hand (prefer using these)", batch.OnHand, "(nothing recorded)");
        AppendSection(sb, "Commonly buy (the familiar ingredient palette)", batch.CommonlyBought, "(nothing recorded)");
        if (batch.ExpiringSoon.Count > 0)
            AppendSection(sb, "USE FIRST — expiring soon", batch.ExpiringSoon, "(none)");
        AppendSection(sb, "Will NOT eat (exclude entirely)", batch.ExcludedFoods, "(none)");
        AppendSection(sb, "Saved recipes (reuse / adapt where they fit)", batch.InspirationRecipes, "(none)");
        AppendSection(sb, "Already planned this run (do NOT repeat)", batch.AvoidNames, "(none)");

        return sb.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder sb, string title, IReadOnlyList<string> items, string emptyText)
    {
        sb.AppendLine($"{title}:");
        sb.AppendLine(items.Count > 0 ? "- " + string.Join("\n- ", items) : emptyText);
        sb.AppendLine();
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
