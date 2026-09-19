using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ShelfAware.Llm.Tests;

// The three small fail-soft advisors had zero coverage until the 7/30 audit (0 of 104 lines across
// them) — each is one short model call whose PARSE and FAIL-OPEN behavior is the actual contract:
// a flaky API must degrade the feature, never break the page that asked.

public class TagAdvisorTests
{
    private static AnthropicTagAdvisor Advisor(IChatClient chat) =>
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

    [Theory]
    // The model returns the household's spelling as asked.
    [InlineData("etc.", "Etc.")]
    [InlineData("soft drink", "Soft Drink")]
    // ⚠️ Both directions, because two fixes in a row each closed one and opened the other. A tag can
    // legitimately end in a period, and a reply that drops it still names that tag; the model also
    // routinely ADDS one, and a reply that appends it still names a tag that has none. Matching on the
    // raw reply alone misses the second, matching on the stripped reply alone misses the first, and a
    // miss here is silent: the dedup returns "no synonym" and the caller coins the near-duplicate the
    // whole advisor exists to prevent, having charged for the act. The first test written for this had
    // the period on BOTH sides, so it passed either way and caught neither.
    [InlineData("Etc", "Etc.")]
    [InlineData("Soft Drink.", "Soft Drink")]
    // ⚠️ And the shapes a LOCAL reading of the reply never knew about. "Which existing tag does
    // this name mean?" belongs to TagVocabulary, which case-folds, collapses whitespace, forgives a
    // trailing plural "s" and one character of typo. For one commit this advisor answered it privately
    // and knew only about a period — so a model that pluralized, or doubled a space, coined a duplicate
    // that Upload.razor's plain-code stage had already caught eight lines before the call that charged
    // for the act. These cases fail against that private reading and pass against the shared one.
    [InlineData("Soft Drinks", "Soft Drink")]
    [InlineData("soft  drink", "Soft Drink")]
    [InlineData("Sofr Drink", "Soft Drink")]
    public async Task A_reply_naming_an_existing_tag_matches_it_whichever_side_has_the_period(
        string reply, string expected)
    {
        string[] existing = ["Etc.", "Soft Drink"];

        Assert.Equal(expected, await Advisor(FakeChatClient.Returning(Responses.Text(reply)))
            .FindSynonymAsync("Miscellaneous", existing));
    }

    [Fact]
    public async Task A_tag_spelled_two_ways_in_unicode_is_one_tag()
    {
        // The household typed a precomposed "\u00e9"; the model answered with an "e" plus a combining
        // accent. One word to anyone reading them, two strings to an ordinal comparison — and two edits
        // apart, so the near-duplicate pass does not rescue it either. Normalizing is part of what
        // "the same tag" means, and it belongs in the one place that owns that question.
        string[] existing = ["Caf\u00e9"];

        Assert.Equal("Caf\u00e9", await Advisor(FakeChatClient.Returning(Responses.Text("Cafe\u0301")))
            .FindSynonymAsync("Coffee Shop", existing));
    }

    [Fact]
    public async Task A_provider_timeout_still_fails_open_even_though_it_arrives_as_a_cancellation()
    {
        // ⚠️ HttpClient's timeout throws TaskCanceledException — an OperationCanceledException — even
        // when the caller passed no token, and no caller of this advisor passes one. An unconditional
        // `catch (OperationCanceledException) { throw; }` therefore reclassified every real provider
        // stall as caller intent and rethrew it into an @onclick handler with no catch and no
        // ErrorBoundary behind it, tearing down the Blazor circuit and losing an in-progress receipt
        // review — under a class summary promising "fails open so a flaky API never blocks tag creation".
        var advisor = Advisor(new ThrowingChatClient(new TaskCanceledException("The request timed out.")));

        Assert.Null(await advisor.FindSynonymAsync("Soda", Existing));
    }

    [Fact]
    public async Task A_caller_that_cancelled_gets_its_cancellation_back()
    {
        // The other side of the same guard: the household closed the tab, and that is not this advisor's
        // to absorb into a "degraded provider" log line.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var advisor = Advisor(new ThrowingChatClient(new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => advisor.FindSynonymAsync("Soda", Existing, cts.Token));
    }

    [Fact]
    public async Task An_exact_spelling_wins_over_the_looser_match()
    {
        // A household with both spellings gets back the one the model actually named.
        string[] existing = ["Etc", "Etc."];

        Assert.Equal("Etc.", await Advisor(FakeChatClient.Returning(Responses.Text("Etc.")))
            .FindSynonymAsync("Miscellaneous", existing));
    }

    [Fact]
    public async Task NONE_means_no_synonym_even_when_a_tag_is_literally_called_None()
    {
        // ⚠️ The sentinel used to be handled here by accident — "NONE" matched no tag, so null came back
        // on its own. Nothing stops a household naming a tag "None", and then the model's way of saying
        // "these are different" would be returned as a synonym for whatever was typed, collapsing two
        // unrelated tags into one.
        Assert.Null(await Advisor(FakeChatClient.Returning(Responses.Text("NONE")))
            .FindSynonymAsync("Snack", ["None", "Soft Drink"]));
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
