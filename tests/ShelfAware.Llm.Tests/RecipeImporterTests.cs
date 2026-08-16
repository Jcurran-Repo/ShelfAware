using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Llm.Tests;

// The recipe importer: the same structured-output + validate-retry machinery as the receipt extractor,
// so the tests pin the same contract — the parse, the "no recipe found" floor (never invent one),
// retry-once on bad JSON, and fail (no retry) on a transport error.
public class RecipeImporterTests
{
    private static AnthropicRecipeImporter Importer(FakeChatClient chat) =>
        new(chat, Options.Create(new LlmOptions()), NullLogger<AnthropicRecipeImporter>.Instance);

    private static readonly RecipePhoto OneImage = new([1, 2, 3], "image/jpeg");

    private const string ValidJson = """
    {
      "found": true,
      "name": "Spaghetti Marinara",
      "blurb": "Weeknight pasta.",
      "calories_per_serving": 480,
      "ingredients": [
        { "name": "Spaghetti", "quantity": "12 oz", "is_main": true },
        { "name": "Marinara sauce", "quantity": "1 jar", "is_main": true },
        { "name": "Parmesan", "quantity": null, "is_main": false }
      ],
      "steps": ["Boil the pasta.", "Warm the sauce.", "Toss together."],
      "tags": ["Dinner", "Italian", "Pasta"]
    }
    """;

    private const string NotFoundJson = """
    { "found": false, "name": null, "blurb": null, "calories_per_serving": null,
      "ingredients": [], "steps": [], "tags": [] }
    """;

    [Fact]
    public async Task Extracts_a_recipe_from_an_image()
    {
        var result = await Importer(FakeChatClient.Returning(Responses.Text(ValidJson))).ImportFromImageAsync(OneImage);

        Assert.True(result.Success);
        var r = result.Recipe!;
        Assert.Equal("Spaghetti Marinara", r.Name);
        Assert.Equal(480, r.CaloriesPerServing);
        Assert.Equal(3, r.Ingredients.Count);
        Assert.Equal("12 oz", r.Ingredients[0].Quantity);
        Assert.True(r.Ingredients[0].IsMain);
        Assert.False(r.Ingredients[2].IsMain); // Parmesan is a seasoning
        Assert.Null(r.Ingredients[2].Quantity);
        Assert.Equal(3, r.Steps.Count);
        Assert.Equal(["Dinner", "Italian", "Pasta"], r.Tags);
    }

    [Fact]
    public async Task Extracts_a_recipe_from_text()
    {
        var result = await Importer(FakeChatClient.Returning(Responses.Text(ValidJson)))
            .ImportFromTextAsync("some pasted recipe");
        Assert.True(result.Success);
        Assert.Equal("Spaghetti Marinara", result.Recipe!.Name);
    }

    [Fact]
    public async Task Attaches_the_image_as_data_content()
    {
        var client = FakeChatClient.Returning(Responses.Text(ValidJson));
        await Importer(client).ImportFromImageAsync(OneImage);

        var user = Assert.Single(client.ReceivedMessages).Last(m => m.Role == ChatRole.User);
        Assert.Single(user.Contents.OfType<DataContent>());
    }

    [Fact]
    public async Task Found_false_is_reported_as_no_recipe_rather_than_invented()
    {
        var result = await Importer(FakeChatClient.Returning(Responses.Text(NotFoundJson))).ImportFromImageAsync(OneImage);

        Assert.False(result.Success);
        Assert.Null(result.Recipe);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Found_false_wins_even_when_a_name_slips_through()
    {
        // The model set found:false yet still filled a name. The flag is authoritative — the whole point
        // of it is to refuse a recipe the source didn't actually contain, so this must NOT build one.
        const string contradictory = """
        { "found": false, "name": "Ghost Recipe", "blurb": null, "calories_per_serving": null,
          "ingredients": [], "steps": [], "tags": [] }
        """;
        var result = await Importer(FakeChatClient.Returning(Responses.Text(contradictory))).ImportFromImageAsync(OneImage);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Blank_text_makes_no_model_call()
    {
        var client = new FakeChatClient(() => Responses.Text(ValidJson));
        var result = await Importer(client).ImportFromTextAsync("   ");

        Assert.False(result.Success);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Retries_once_on_unparseable_output()
    {
        var client = new FakeChatClient(() => Responses.Text("not json at all"), () => Responses.Text(ValidJson));
        var result = await Importer(client).ImportFromTextAsync("text");

        Assert.True(result.Success);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task An_api_error_fails_without_retrying()
    {
        var client = new FakeChatClient(() => throw new HttpRequestException("down"));
        var result = await Importer(client).ImportFromImageAsync(OneImage);

        Assert.False(result.Success);
        Assert.Equal(1, client.CallCount); // transport errors are not retried
    }
}
