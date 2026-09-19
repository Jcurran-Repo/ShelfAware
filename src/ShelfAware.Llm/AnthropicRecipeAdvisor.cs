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

    public async Task<IReadOnlyList<RecipeSuggestion>?> SuggestAsync(
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

        // ⚠️ The PARSE is inside the try, like all four sibling services (AnthropicReceiptExtractor,
        // AnthropicRecipeImporter, AnthropicShelfCensusReader, AnthropicMealPlanGenerator — the last says
        // in as many words that a long structured reply "can come back TRUNCATED … which would otherwise
        // throw and take the whole plan down"). MaxOutputTokens is 4096 for up to three full recipes with
        // steps, so a cut-off reply is a live case, and JsonDocument.Parse throws on one. The commit that
        // wrapped this class's transport call left the parse outside and made it the only service in the
        // assembly guarding half of what its siblings guard.
        List<RecipeSuggestion> suggestions;
        try
        {
            var response = await _chat.GetResponseAsync(messages, options, cancellationToken);
            suggestions = RecipeJson.Parse(response.Text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } // whose cancellation: see ProviderCancellationSiteTests
        catch (Exception ex)
        {
            // ⚠️ This class was the ONE service in the assembly whose provider calls sat in no try at all,
            // and it stayed that way through a commit that converted nine sibling guards and added a build
            // rule to hold them — because a rule that inspects catch clauses cannot see a missing one.
            // Recipes.razor rethrew the escape unfiltered, so a slow provider tore the circuit out from
            // under a household that had just typed a request.
            _logger.LogError(ex, "Recipe suggestion call to the model failed.");
            return null; // couldn't reach it — NOT the same as "no ideas", see IRecipeAdvisor
        }

        // ⚠️ The SAME question AdaptAsync asks, through the same predicate. For one commit this method
        // asked `suggestions.Count == 0` — present — while RecipeReply.Landed eight lines below meant
        // present AND named, and RecipeJson.Parse keeps an unnamed entry (`name` falls back to ""). So a
        // reply of [{"name":"  "}] was refunded by one method and charged in full by the other, and the
        // screen drew a card with a blank title. The consolidated definition shipped narrower than the
        // sites around it, which is the failure this arc keeps repeating — see CLAUDE.md item 41.
        var landed = suggestions.Where(s => s.Landed()).ToList();
        _logger.LogInformation("Recipe advisor returned {Count} usable suggestion(s) of {Parsed} parsed for {OnHand} on-hand item(s).",
            landed.Count, suggestions.Count, onHand.Count);
        // recipe-suggest-system.txt rule 2 says "Suggest 1-3 recipe ideas" with no way to decline, so
        // nothing usable coming back is the model failing, not an honest "nothing here" — §4.w refunds it.
        if (landed.Count == 0) return landed;
        action.Answered();
        return landed;
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

        // ⚠️ The parse is inside the try for the reason given in SuggestAsync above: a truncated reply
        // throws out of JsonDocument.Parse, and every sibling service in this assembly wraps call+parse
        // together.
        RecipeSuggestion? adapted;
        try
        {
            var response = await _chat.GetResponseAsync(messages, options, cancellationToken);
            adapted = RecipeJson.Parse(response.Text).FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } // whose cancellation: see ProviderCancellationSiteTests
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recipe adaptation call to the model failed.");
            return null; // RecipeAdapter reads null as "couldn't adapt", which is what happened
        }

        _logger.LogInformation("Recipe advisor adapted \"{Name}\" (produced result: {HasResult}).", recipe.Name, adapted is not null);
        // ⚠️ Settled on a NAMED adaptation, not on the call returning. A reply that parses to nothing,
        // or to a variant with no name, is one RecipeAdapter turns into "Couldn't adapt {recipe} right
        // now." — and it used to charge a full credit for it, then invite the household to press the
        // button again and charge again. Charging for a turn the household demonstrably did not receive
        // is the thing docs/subscription-plan.md §4.w exists to stop; the old comment here called it
        // "including this can't be adapted to what you have", which is a different and honest answer the
        // model never actually gave.
        //
        // ⚠️ The neighbouring case — an adaptation that came back fine but IGNORED the swap the household
        // picked — is not settled here, and deliberately so: this method cannot see which swap was chosen.
        // It is settled where that fact lives, in RecipeAdapter, which now LABELS the variant and keeps the
        // charge rather than rejecting it and inviting a paid retry (asking is what is paid for, §4.w — see
        // AdaptResult.SwapIgnored). That was the open "Jordan's call" this comment used to flag; it was
        // decided and implemented, so there is nothing left owing here.
        if (!adapted.Landed()) return adapted;
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
