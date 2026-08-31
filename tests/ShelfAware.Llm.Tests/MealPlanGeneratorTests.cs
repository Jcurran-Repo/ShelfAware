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

    private static MealPlanBatch Batch(MealPlanSettings? settings = null) => new(
        Slots: [new PlannedSlot(0, MealSlot.Dinner), new PlannedSlot(1, MealSlot.Dinner)],
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
        Assert.Contains("Chicken Breast", user);            // on hand
        Assert.Contains("Ground Beef", user);               // commonly buy — the familiar palette
        Assert.Contains("mushrooms", user);                 // won't eat
        Assert.Contains("Weeknight Tacos", user);           // saved recipe (adapt-known)
        Assert.Contains("Already planned this run", user);  // the variety section…
        Assert.Contains("Spaghetti Marinara", user);        // …with the avoid name in it
        Assert.Contains("Invent: no", user);                // default: don't invent
    }

    [Fact]
    public async Task The_calorie_target_effort_and_invent_flag_reach_the_prompt()
    {
        var client = FakeChatClient.Returning(Responses.Text(TwoMeals));
        var settings = new MealPlanSettings { CaloriesPerMeal = 500, Effort = TimeEffort.Quick, Invent = true };

        await Generator(client).GenerateAsync(Batch(settings));

        var user = UserPrompt(client);
        Assert.Contains("(~500 cal)", user);   // the per-meal target rides on each slot line
        Assert.Contains("Effort: quick", user);
        Assert.Contains("Invent: yes", user);  // the escape hatch reaches the model
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
