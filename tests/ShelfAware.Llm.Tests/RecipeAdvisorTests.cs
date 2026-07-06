using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
