using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Llm;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// The meal-plan generator, driven through a FakeChatClient so no live API is needed: it produces one
/// recipe per requested slot (parsed via the shared RecipeJson), and the household's setup + pantry + the
/// already-planned names all reach the model — the argument for "adapt-known-first, don't invent, keep it
/// varied" is that the context actually rides on the call.
/// </summary>
public class MealPlanGeneratorTests
{
    private static AnthropicMealPlanGenerator Generator(FakeChatClient chat) =>
        new(chat, Options.Create(new LlmOptions()), NullLogger<AnthropicMealPlanGenerator>.Instance);

    private const string TwoMeals = """
    { "recipes": [
      { "name": "Sheet-Pan Chicken", "blurb": "Easy.", "ingredients": [
          { "name": "chicken breast", "main": true, "matched_product": "Chicken Breast", "quantity": "2 lbs" } ],
        "steps": ["Roast it."], "calories_per_serving": 480 },
      { "name": "Beef Chili", "blurb": "Cozy.", "ingredients": [
          { "name": "ground beef", "main": true, "matched_product": null, "quantity": "1 lb" } ],
        "steps": ["Simmer it."], "calories_per_serving": 520 }
    ] }
    """;

    private static MealPlanBatch Batch(MealPlanSettings? settings = null, IReadOnlyList<PlannedSlot>? slots = null) => new(
        Slots: slots ?? [new PlannedSlot(0, MealSlot.Dinner, null, TimeEffort.Standard), new PlannedSlot(1, MealSlot.Dinner, null, TimeEffort.Standard)],
        Settings: settings ?? new MealPlanSettings(),
        OnHand: ["Chicken Breast", "White Rice"],
        CommonlyBought: ["Ground Beef", "Bell Peppers"],
        ExpiringSoon: [],
        ExcludedFoods: ["mushrooms"],
        InspirationRecipes: ["Weeknight Tacos"],
        AvoidNames: ["Spaghetti Marinara"]);

    private static string UserPrompt(FakeChatClient client) =>
        client.ReceivedMessages[0].Single(m => m.Role == ChatRole.User).Text;

    [Fact]
    public async Task Generates_one_recipe_per_slot_and_writes_the_context_into_the_prompt()
    {
        var client = FakeChatClient.Returning(Responses.Text(TwoMeals));

        var meals = await Generator(client).GenerateAsync(Batch());

        Assert.Equal(2, meals.Count);
        Assert.Equal("Sheet-Pan Chicken", meals[0].Name);
        Assert.Equal(480, meals[0].CaloriesPerServing);
        Assert.Contains(meals[0].Ingredients, i => i.Name == "chicken breast" && i.Have); // grounded match rides through

        var user = UserPrompt(client);
        Assert.Contains("1. Day 1 dinner", user);          // slots listed IN ORDER, 1-based
        Assert.Contains("2. Day 2 dinner", user);
        Assert.Contains("standard effort", user);           // each slot names its effort
        Assert.Contains("Chicken Breast", user);            // on hand
        Assert.Contains("Ground Beef", user);               // commonly buy — the familiar palette
        Assert.Contains("mushrooms", user);                 // won't eat
        Assert.Contains("Weeknight Tacos", user);           // saved recipe (adapt-known)
        Assert.Contains("Already planned this run", user);  // the variety section…
        Assert.Contains("Spaghetti Marinara", user);        // …with the avoid name in it
        Assert.Contains("Invent: no", user);                // default: don't invent
    }

    [Fact]
    public async Task Each_slot_carries_its_own_calorie_and_effort_target()
    {
        // The whole point of per-meal settings: a snack asks for 150 cal / quick while dinner asks for
        // 600 cal / ambitious — on the SAME plan.
        var client = FakeChatClient.Returning(Responses.Text(TwoMeals));
        var slots = new[]
        {
            new PlannedSlot(0, MealSlot.Snack, 150, TimeEffort.Quick),
            new PlannedSlot(0, MealSlot.Dinner, 600, TimeEffort.Ambitious),
        };

        await Generator(client).GenerateAsync(Batch(new MealPlanSettings { Invent = true }, slots));

        var user = UserPrompt(client);
        Assert.Contains("snack (quick effort, ~150 cal)", user);
        Assert.Contains("dinner (ambitious effort, ~600 cal)", user);
        Assert.Contains("Invent: yes", user);  // the escape hatch reaches the model
    }

    [Fact]
    public async Task Prefer_leftovers_reaches_the_prompt_only_when_on()
    {
        var off = FakeChatClient.Returning(Responses.Text(TwoMeals));
        await Generator(off).GenerateAsync(Batch());
        Assert.DoesNotContain("Prefer leftovers", UserPrompt(off));

        var on = FakeChatClient.Returning(Responses.Text(TwoMeals));
        await Generator(on).GenerateAsync(Batch(new MealPlanSettings { PreferLeftovers = true }));
        Assert.Contains("Prefer leftovers: yes", UserPrompt(on));
    }

    [Fact]
    public async Task Parses_the_servings_the_model_reports()
    {
        const string withServings = """
        { "recipes": [
          { "name": "Sheet-Pan Chicken", "blurb": "Easy.", "ingredients": [
              { "name": "chicken breast", "main": true, "matched_product": "Chicken Breast", "quantity": "2 lbs" } ],
            "steps": ["Roast it."], "calories_per_serving": 480, "servings": 4 }
        ] }
        """;
        var client = FakeChatClient.Returning(Responses.Text(withServings));

        var meals = await Generator(client).GenerateAsync(
            Batch(slots: [new PlannedSlot(0, MealSlot.Dinner, null, TimeEffort.Standard)]));

        Assert.Equal(4, meals[0].Servings);
    }

    [Fact]
    public async Task Servings_is_null_when_the_model_omits_it()
    {
        // Structured output guarantees the key, but the parse must not assume it (TwoMeals has no "servings").
        var client = FakeChatClient.Returning(Responses.Text(TwoMeals));
        var meals = await Generator(client).GenerateAsync(Batch());
        Assert.Null(meals[0].Servings);
    }

    [Fact]
    public async Task Expiring_items_are_flagged_use_first_only_when_present()
    {
        // Empty → no "USE FIRST" section (an empty instruction would just be noise the model might over-weight).
        var client = FakeChatClient.Returning(Responses.Text(TwoMeals));
        await Generator(client).GenerateAsync(Batch());
        Assert.DoesNotContain("USE FIRST", UserPrompt(client));

        var client2 = FakeChatClient.Returning(Responses.Text(TwoMeals));
        await Generator(client2).GenerateAsync(Batch() with { ExpiringSoon = ["Baby Spinach"] });
        var user = UserPrompt(client2);
        Assert.Contains("USE FIRST", user);
        Assert.Contains("Baby Spinach", user);
    }
}
