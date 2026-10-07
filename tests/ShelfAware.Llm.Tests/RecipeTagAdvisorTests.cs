using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Tagging;

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
    public async Task NONE_with_a_trailing_period_still_reads_as_no_tags()
    {
        // The model routinely ends a reply with a period; "NONE." must be the sentinel, not a literal tag.
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text("NONE.")));
        Assert.Empty(await advisor.SuggestAsync("Mystery Dish", NoIngredients, NoKnown));
    }

    [Fact]
    public async Task A_stray_NONE_token_among_real_tags_is_dropped()
    {
        // A malformed "NONE, Dinner" reply (the sentinel only catches a lone NONE) must not store NONE.
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text("NONE, Dinner")));
        Assert.Equal(["Dinner"], await advisor.SuggestAsync("Some Dish", NoIngredients, NoKnown));
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
    public async Task Known_tags_are_bounded_and_ranked_against_the_recipe()
    {
        // ⚠️ The household's whole vocabulary used to ride into every charged call here, unbounded in its
        // size. The bound is by nearness to the recipe — "Pasta" is listed last, where a plain Take would
        // have dropped it, and ranks first because its spelling is an ingredient; what gives way is the
        // far end of the fillers.
        var fillers = Enumerable.Range(0, TagVocabulary.PromptVocabularyLimit + 10).Select(i => $"Filler {i:00}");
        var known = fillers.Append("Pasta").ToList();
        var chat = FakeChatClient.Returning(Responses.Text("Dinner"));

        await Advisor(chat).SuggestAsync("Spaghetti", ["pasta", "tomato"], known);

        var prompt = chat.ReceivedMessages[0].Last().Text;
        var line = prompt.Split('\n').Single(l => l.StartsWith("Prefer these existing tags", StringComparison.Ordinal));
        var listed = line[(line.IndexOf(':') + 1)..].TrimEnd('.').Split(',', StringSplitOptions.TrimEntries);
        Assert.Equal(TagVocabulary.PromptVocabularyLimit, listed.Length);
        Assert.Equal("Pasta", listed[0]);
        Assert.DoesNotContain("Filler 49", listed);
    }

    [Fact]
    public async Task Fails_open_on_an_api_error()
    {
        var chat = new FakeChatClient(() => throw new HttpRequestException("down"));
        Assert.Empty(await Advisor(chat).SuggestAsync("Anything", NoIngredients, NoKnown));
    }
}
