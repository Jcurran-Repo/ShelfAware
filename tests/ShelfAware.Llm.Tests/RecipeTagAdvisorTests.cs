using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ShelfAware.Llm.Tests;

// The recipe-tag advisor: one short model call whose PARSE and FAIL-OPEN behavior is the whole contract —
// a flaky API leaves a recipe untagged, it never breaks the cookbook that asked.
public class RecipeTagAdvisorTests
{
    private static AnthropicRecipeTagAdvisor Advisor(FakeChatClient chat) =>
        new(chat, Options.Create(new LlmOptions()), NullLogger<AnthropicRecipeTagAdvisor>.Instance);

    private static readonly string[] NoIngredients = [];
    private static readonly string[] NoKnown = [];

    [Fact]
    public async Task Parses_a_comma_list_into_tags()
    {
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text("Dinner, Italian, Pasta")));
        Assert.Equal(["Dinner", "Italian", "Pasta"],
            await advisor.SuggestAsync("Spaghetti", ["spaghetti", "marinara"], NoKnown));
    }

    [Fact]
    public async Task NONE_returns_no_tags()
    {
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text("NONE")));
        Assert.Empty(await advisor.SuggestAsync("Mystery Dish", NoIngredients, NoKnown));
    }

    [Fact]
    public async Task A_blank_recipe_name_makes_no_model_call()
    {
        var chat = new FakeChatClient(() => Responses.Text("Dinner"));
        Assert.Empty(await Advisor(chat).SuggestAsync("   ", NoIngredients, NoKnown));
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task Caps_the_number_of_tags()
    {
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text("A, B, C, D, E, F, G")));
        Assert.Equal(5, (await advisor.SuggestAsync("Big One", NoIngredients, NoKnown)).Count); // MaxItems
    }

    [Fact]
    public async Task Fails_open_on_an_api_error()
    {
        var chat = new FakeChatClient(() => throw new HttpRequestException("down"));
        Assert.Empty(await Advisor(chat).SuggestAsync("Anything", NoIngredients, NoKnown));
    }
}
