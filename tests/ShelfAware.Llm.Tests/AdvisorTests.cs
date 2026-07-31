using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ShelfAware.Llm.Tests;

// The three small fail-soft advisors had zero coverage until the 7/30 audit (0 of 104 lines across
// them) — each is one short model call whose PARSE and FAIL-OPEN behavior is the actual contract:
// a flaky API must degrade the feature, never break the page that asked.

public class TagAdvisorTests
{
    private static AnthropicTagAdvisor Advisor(FakeChatClient chat) =>
        new(chat, Options.Create(new LlmOptions()), NullLogger<AnthropicTagAdvisor>.Instance);

    private static readonly string[] Existing = ["Condiment", "Soft Drink", "Paper Goods"];

    [Fact]
    public async Task A_synonym_reply_returns_the_existing_tags_own_spelling()
    {
        // The reply is matched case-insensitively but the EXISTING spelling is what comes back —
        // the caller stores it, so the vocabulary must not fork on the model's casing.
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text("soft drink")));

        Assert.Equal("Soft Drink", await advisor.FindSynonymAsync("Soda", Existing));
    }

    [Fact]
    public async Task A_reply_that_is_not_an_existing_tag_is_treated_as_no_synonym()
    {
        // Only a tag from the list may come back. A model that hallucinates a NEW tag ("Beverages")
        // must read as "genuinely different" — otherwise the dedup would coin the very near-dupe it
        // exists to prevent.
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text("Beverages")));

        Assert.Null(await advisor.FindSynonymAsync("Soda", Existing));
    }

    [Fact]
    public async Task NONE_means_no_synonym()
    {
        Assert.Null(await Advisor(FakeChatClient.Returning(Responses.Text("NONE")))
            .FindSynonymAsync("Snack", Existing));
    }

    [Fact]
    public async Task A_blank_candidate_or_empty_vocabulary_short_circuits_without_a_call()
    {
        var client = FakeChatClient.Returning(Responses.Text("unused"));
        var advisor = Advisor(client);

        Assert.Null(await advisor.FindSynonymAsync("   ", Existing));
        Assert.Null(await advisor.FindSynonymAsync("Soda", []));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task A_model_failure_fails_open_so_tag_creation_is_never_blocked()
    {
        var advisor = Advisor(new FakeChatClient(() => throw new HttpRequestException("down")));

        Assert.Null(await advisor.FindSynonymAsync("Soda", Existing));
    }
}

public class ProductSubstituteAdvisorTests
{
    private static AnthropicProductSubstituteAdvisor Advisor(FakeChatClient chat) =>
        new(chat, Options.Create(new LlmOptions()), NullLogger<AnthropicProductSubstituteAdvisor>.Instance);

    [Fact]
    public async Task Parses_the_comma_list_lowercased_deduped_and_without_the_products_own_name()
    {
        // Everything the parse promises in one realistic reply: trailing period trimmed, casing
        // folded, an echo of the product itself dropped, and a case-different dupe collapsed.
        var advisor = Advisor(FakeChatClient.Returning(
            Responses.Text("Chicken Breast, chicken cutlet, Chicken Breast Tenderloins, CHICKEN BREAST, chicken tenders.")));

        var result = await advisor.SuggestAsync("Chicken Breast Tenderloins", "Meat");

        Assert.Equal(["chicken breast", "chicken cutlet", "chicken tenders"], result);
    }

    [Fact]
    public async Task The_list_caps_at_eight_items()
    {
        var advisor = Advisor(FakeChatClient.Returning(
            Responses.Text("a, b, c, d, e, f, g, h, i, j")));

        Assert.Equal(8, (await advisor.SuggestAsync("Thing", "Pantry")).Count);
    }

    [Fact]
    public async Task NONE_and_blank_input_both_mean_no_suggestions()
    {
        var client = FakeChatClient.Returning(Responses.Text("NONE"));
        var advisor = Advisor(client);

        Assert.Empty(await advisor.SuggestAsync("Salt", "Pantry"));   // the model's "no meaningful swaps"
        Assert.Empty(await advisor.SuggestAsync("  ", "Pantry"));     // nothing to ask about
        Assert.Equal(1, client.CallCount);                            // only the first consulted the model
    }

    [Fact]
    public async Task A_model_failure_fails_open_with_an_empty_list()
    {
        var advisor = Advisor(new FakeChatClient(() => throw new HttpRequestException("down")));

        Assert.Empty(await advisor.SuggestAsync("Chicken Breast Tenderloins", "Meat"));
    }
}

public class IngredientAlternativesAdvisorTests
{
    private static AnthropicIngredientAlternativesAdvisor Advisor(FakeChatClient chat) =>
        new(chat, Options.Create(new LlmOptions()), NullLogger<AnthropicIngredientAlternativesAdvisor>.Instance);

    [Fact]
    public async Task Parses_swaps_without_repeating_the_ingredient_itself()
    {
        var advisor = Advisor(FakeChatClient.Returning(
            Responses.Text("chicken thighs, Chicken Breast, chicken tenderloins")));

        Assert.Equal(["chicken thighs", "chicken tenderloins"],
            await advisor.SuggestAsync("chicken breast"));
    }

    [Fact]
    public async Task The_cloud_caps_at_six_bubbles()
    {
        var advisor = Advisor(FakeChatClient.Returning(Responses.Text("a, b, c, d, e, f, g, h")));

        Assert.Equal(6, (await advisor.SuggestAsync("beef")).Count);
    }

    [Fact]
    public async Task NONE_and_blank_input_both_mean_an_empty_cloud()
    {
        var client = FakeChatClient.Returning(Responses.Text("NONE"));
        var advisor = Advisor(client);

        Assert.Empty(await advisor.SuggestAsync("saffron"));
        Assert.Empty(await advisor.SuggestAsync(""));
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task A_model_failure_fails_open_so_the_swap_cloud_never_breaks_the_page()
    {
        var advisor = Advisor(new FakeChatClient(() => throw new HttpRequestException("down")));

        Assert.Empty(await advisor.SuggestAsync("chicken breast"));
    }
}
