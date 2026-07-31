using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Recipes;
using ShelfAware.Llm;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// Parse coverage for the recipe advisor's structured output — in particular the v2 cooking `steps`
/// array — driven through a FakeChatClient so no live API is needed.
/// </summary>
public class RecipeAdvisorTests
{
    private static AnthropicRecipeAdvisor Advisor(FakeChatClient chat) =>
        new(chat, Options.Create(new LlmOptions()), NullLogger<AnthropicRecipeAdvisor>.Instance);

    [Fact]
    public async Task Parses_steps_ingredients_and_grounded_matches()
    {
        const string json = """
        {
          "recipes": [
            {
              "name": "Feta Avocado Toast",
              "blurb": "Creamy avocado with salty feta on toast.",
              "ingredients": [
                { "name": "Avocado", "main": true, "matched_product": "Hass Avocados", "quantity": "1" },
                { "name": "Feta", "main": true, "matched_product": null, "quantity": "1/4 cup" },
                { "name": "Olive oil", "main": false, "matched_product": null, "quantity": "to taste" }
              ],
              "steps": [
                "Toast the bread.",
                "Mash the avocado and spread it on.",
                "Crumble feta over the top and drizzle with oil."
              ],
              "calories_per_serving": 520
            }
          ]
        }
        """;
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text(json)));

        var results = await advisor.SuggestAsync("mediterranean", ["Hass Avocados"], []);

        var recipe = Assert.Single(results);
        Assert.Equal("Feta Avocado Toast", recipe.Name);
        Assert.Equal(3, recipe.Steps.Count);
        Assert.Equal("Toast the bread.", recipe.Steps[0]);
        Assert.Equal("Crumble feta over the top and drizzle with oil.", recipe.Steps[2]);
        Assert.Equal(520, recipe.CaloriesPerServing);
        // Grounded match -> Have; unmatched main -> shows up in ToGrab.
        Assert.Contains(recipe.Ingredients, i => i.Name == "Avocado" && i.Have);
        Assert.Contains(recipe.ToGrab, g => g.Name == "Feta");
        // Free-form quantities parse through (and a missing one would be null — see the no-steps test).
        Assert.Equal("1", recipe.Ingredients.Single(i => i.Name == "Avocado").Quantity);
        Assert.Equal("to taste", recipe.Ingredients.Single(i => i.Name == "Olive oil").Quantity);
    }

    [Fact]
    public async Task A_response_with_no_recipes_property_is_an_empty_list_not_a_crash()
    {
        // Structured outputs make this near-impossible, but the parse must not assume it.
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text("{}")));

        Assert.Empty(await advisor.SuggestAsync("anything", [], []));
    }

    [Fact]
    public async Task Adapt_writes_the_swap_the_pantry_and_the_recipe_into_the_prompt()
    {
        // AdaptAsync had zero coverage until the 7/30 audit — and its prompt assembly is what the
        // adapter's ignored-swap guard DEPENDS on: the guard can only reject a result the model was
        // actually told about. Pin that the mandatory swap, the curated "also works as" lists, the
        // seasoning markers, and the numbered steps all reach the model.
        const string json = """
        { "recipes": [ { "name": "Pan-Seared Chicken Thighs", "blurb": "Adapted.",
          "ingredients": [ { "name": "chicken thighs", "main": true, "matched_product": null, "quantity": "2 lbs" } ],
          "steps": ["Sear the thighs."], "calories_per_serving": 450 } ] }
        """;
        var client = FakeChatClient.Returning(Responses.Text(json));

        var result = await Advisor(client).AdaptAsync(
            new RecipeToAdapt(
                "Pan-Seared Chicken", "Weeknight easy.",
                [new AdaptIngredient("chicken breast", IsMain: true, "2 lbs"), new AdaptIngredient("paprika", IsMain: false)],
                ["Sear the chicken.", "Rest it."]),
            [new PantryProduct("Chicken Thighs", ["chicken breast", "chicken cutlet"])],
            ["mushrooms"],
            preference: "Use chicken thighs in place of chicken breast.");

        Assert.NotNull(result);
        Assert.Equal("Pan-Seared Chicken Thighs", result.Name);
        Assert.Equal(450, result.CaloriesPerServing);

        var userMessage = client.ReceivedMessages[0].Single(m => m.Role == Microsoft.Extensions.AI.ChatRole.User).Text;
        Assert.Contains("USER'S CHOSEN SWAP (MANDATORY", userMessage);
        Assert.Contains("Use chicken thighs in place of chicken breast.", userMessage);
        Assert.Contains("2 lbs chicken breast", userMessage);              // quantity rides with the name
        Assert.Contains("paprika (seasoning)", userMessage);               // mains and seasonings are told apart
        Assert.Contains("1. Sear the chicken.", userMessage);              // steps arrive numbered
        Assert.Contains("Chicken Thighs (also works as: chicken breast, chicken cutlet)", userMessage); // rule 9
        Assert.Contains("mushrooms", userMessage);                         // the won't-eat list
    }

    [Fact]
    public async Task Adapt_returns_null_when_the_model_produces_no_recipe()
    {
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text("""{ "recipes": [] }""")));

        Assert.Null(await advisor.AdaptAsync(
            new RecipeToAdapt("X", null, [new AdaptIngredient("beef", true)], []), [], []));
    }

    [Fact]
    public async Task Tolerates_a_recipe_with_no_steps()
    {
        const string json = """
        { "recipes": [ { "name": "Snack Plate", "blurb": "Just assemble.", "ingredients": [], "steps": [] } ] }
        """;
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text(json)));

        var recipe = Assert.Single(await advisor.SuggestAsync("quick", [], []));

        Assert.Empty(recipe.Steps);
        Assert.Null(recipe.CaloriesPerServing); // absent in the response -> null, not a parse failure
    }

    [Fact]
    public async Task Trims_and_drops_blank_steps()
    {
        const string json = """
        { "recipes": [ { "name": "X", "blurb": "y", "ingredients": [],
          "steps": ["  Chop the onion.  ", "", "   ", "Fry it."] } ] }
        """;
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text(json)));

        var recipe = Assert.Single(await advisor.SuggestAsync("x", [], []));

        Assert.Equal(2, recipe.Steps.Count);
        Assert.Equal("Chop the onion.", recipe.Steps[0]);
        Assert.Equal("Fry it.", recipe.Steps[1]);
    }
}
