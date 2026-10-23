using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Census;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// ⚠️ Every AI call names the model <see cref="IAiModelChoice"/> gives it, read when the call is made.
///
/// <para>The defect this pins: every service used to read the server's <see cref="LlmOptions"/>, so a model a
/// BYOK visitor picked in Settings reached only the client's binding, and the call's own <c>ModelId</c> — which
/// wins — still named the server's. The suite stayed green throughout, because every test built its services
/// from a default <see cref="LlmOptions"/>, where the server's model and the "chosen" one are the same string.
/// So each case here starts the choice on one model and moves it to another AFTER the service exists: the
/// visitor's settings reach the circuit after its services are constructed, and a service that captured the
/// model in its constructor would fail exactly the way production did.</para>
/// </summary>
public class ModelChoiceTests
{
    private static readonly IReadOnlyList<ReceiptAttachment> OneReceipt = [new([1, 2, 3], "image/jpeg")];
    private static readonly IReadOnlyList<ShelfPhoto> OnePhoto = [new([1, 2, 3], "image/jpeg")];
    private static readonly RecipePhoto OneRecipePhoto = new([1, 2, 3], "image/jpeg");

    private const string PickedExtraction = "visitor-extraction-model";
    private const string PickedChat = "visitor-chat-model";

    /// <summary>A choice that changes under the service, as a circuit's does when the browser's settings land.</summary>
    private sealed class MovingChoice : IAiModelChoice
    {
        public string ExtractionModel { get; set; } = "server-extraction-model";
        public string ChatModel { get; set; } = "server-chat-model";
    }

    public static TheoryData<string> Calls => [.. CallSites.Keys];

    /// <summary>Every call site that builds a <see cref="ChatOptions"/>, and which of the two models it is
    /// meant to name. Two services have two sites each, and they don't all ask for the same model: the
    /// importer reads a PHOTO with the extraction model and structures pasted TEXT with the chat model.</summary>
    private static readonly Dictionary<string, (string Expected, Func<IChatClient, IAiModelChoice, Func<Task>> Build)> CallSites = new()
    {
        ["receipt extraction"] = (PickedExtraction, (c, m) =>
            Call(new AnthropicReceiptExtractor(c, m, NullLogger<AnthropicReceiptExtractor>.Instance), s => s.ExtractAsync(OneReceipt))),
        ["shelf census"] = (PickedExtraction, (c, m) =>
            Call(new AnthropicShelfCensusReader(c, m, NullLogger<AnthropicShelfCensusReader>.Instance), s => s.ReadAsync(OnePhoto))),
        ["recipe import from a photo"] = (PickedExtraction, (c, m) =>
            Call(new AnthropicRecipeImporter(c, m, NullLogger<AnthropicRecipeImporter>.Instance), s => s.ImportFromImageAsync(OneRecipePhoto))),
        ["recipe import from text"] = (PickedChat, (c, m) =>
            Call(new AnthropicRecipeImporter(c, m, NullLogger<AnthropicRecipeImporter>.Instance), s => s.ImportFromTextAsync("Toast: toast the bread."))),
        ["tag synonym"] = (PickedExtraction, (c, m) =>
            Call(new AnthropicTagAdvisor(c, m, NullLogger<AnthropicTagAdvisor>.Instance), s => s.FindSynonymAsync("Snack", ["Condiment"]))),
        ["recipe tags"] = (PickedExtraction, (c, m) =>
            Call(new AnthropicRecipeTagAdvisor(c, m, NullLogger<AnthropicRecipeTagAdvisor>.Instance), s => s.SuggestAsync("Toast", ["Bread"], ["Breakfast"]))),
        ["product substitutes"] = (PickedExtraction, (c, m) =>
            Call(new AnthropicProductSubstituteAdvisor(c, m, NullLogger<AnthropicProductSubstituteAdvisor>.Instance), s => s.SuggestAsync("Saffron", "Pantry"))),
        ["ingredient alternatives"] = (PickedExtraction, (c, m) =>
            Call(new AnthropicIngredientAlternativesAdvisor(c, m, NullLogger<AnthropicIngredientAlternativesAdvisor>.Instance), s => s.SuggestAsync("Saffron"))),
        ["recipe suggestions"] = (PickedChat, (c, m) =>
            Call(new AnthropicRecipeAdvisor(c, m, NullLogger<AnthropicRecipeAdvisor>.Instance), s => s.SuggestAsync("anything", [], []))),
        ["recipe adapt"] = (PickedChat, (c, m) =>
            Call(new AnthropicRecipeAdvisor(c, m, NullLogger<AnthropicRecipeAdvisor>.Instance), s => s.AdaptAsync(
                new RecipeToAdapt("Paella", null, [new AdaptIngredient("Saffron", true, "1 g")], []), [], []))),
        ["meal plan"] = (PickedChat, (c, m) =>
            Call(new AnthropicMealPlanGenerator(c, m, NullLogger<AnthropicMealPlanGenerator>.Instance), s => s.GenerateAsync(new MealPlanBatch(
                [new PlannedSlot(0, MealSlot.Dinner, null, TimeEffort.Standard)], new MealPlanSettings(), [], [], [], [], [], [])))),
        ["chat turn"] = (PickedChat, (c, m) =>
            Call(new AnthropicPantryChat(c, m, new FakePantryStore(), NullLogger<AnthropicPantryChat>.Instance), s => s.HandleAsync("hello"))),
    };

    private static Func<Task> Call<T>(T service, Func<T, Task> call) => () => call(service);

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task A_call_names_the_model_chosen_when_it_runs_not_when_its_service_was_built(string call)
    {
        var (expected, build) = CallSites[call];
        var choice = new MovingChoice();
        // Enough replies for any retry the service makes; what they say doesn't matter, only what was asked.
        var client = FakeChatClient.Returning([.. Enumerable.Repeat(Responses.Text("NONE"), 4)]);
        var invoke = build(client, choice); // the service exists from here, holding the server's models

        choice.ExtractionModel = PickedExtraction;
        choice.ChatModel = PickedChat;
        await invoke();

        Assert.NotEmpty(client.ReceivedOptions);
        Assert.All(client.ReceivedOptions, o => Assert.Equal(expected, o?.ModelId));
    }

    [Fact]
    public void No_AI_service_reads_the_server_config_for_its_model()
    {
        // The rule, held where a new service would break it: nothing in this assembly takes the server's
        // LlmOptions any more, so a service added later can only get a model by asking IAiModelChoice.
        // ⚠️ And it proves it looked: an empty scan would pass this vacuously, so it also counts the services
        // that DO ask the choice, which is every row of the table above.
        var constructors = typeof(IAiModelChoice).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .ToList();

        Assert.DoesNotContain(constructors, c => c.GetParameters().Any(p => p.ParameterType == typeof(IOptions<LlmOptions>)));
        var asking = constructors.Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(IAiModelChoice)))
            .Select(c => c.DeclaringType!).Distinct().Count();
        Assert.Equal(10, asking);
    }
}
