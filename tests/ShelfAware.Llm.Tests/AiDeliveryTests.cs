using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Census;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// ⚠️ What every AI act SETTLES FOR — the rule Jordan decided on 2026-09-19: the household pays when the
/// assistant answered, whatever the answer said, and gets its credits back when the call failed.
///
/// <para>An honest "there is no recipe in that photo", "no substitutes for this", "nothing on that shelf"
/// is an answer. It cost a provider call, it is often the RIGHT answer, and refunding it would mean
/// paying the assistant more for inventing a recipe than for telling the truth about a blurry photo.
/// What comes back is the act that failed: the provider unreachable, a reply we could not read, a
/// cancelled turn — the household asked and got nothing.</para>
///
/// <para>⚠️ These exist because nothing pinned it. Until this file, no test in this suite mentioned
/// <c>AiActionScope</c> at all: nine services each decided "did it deliver?" with their own arithmetic
/// (<c>suggestions.Count &gt; 0</c> here, <c>adapted is not null</c> there), two of them were already
/// right by accident, and the whole set could be changed without a single test going red. The build rule
/// in <c>AiActionScopeSiteTests</c> only asks that a service settles SOMETHING — which branch it settles
/// on is each service's own to hold, and this is where they hold it.</para>
/// </summary>
public class AiDeliveryTests
{
    private static readonly IReadOnlyList<ReceiptAttachment> OneReceipt = [new([1, 2, 3], "image/jpeg")];
    private static readonly IReadOnlyList<ShelfPhoto> OnePhoto = [new([1, 2, 3], "image/jpeg")];
    private static readonly RecipePhoto OneRecipePhoto = new([1, 2, 3], "image/jpeg");
    private static readonly string[] ExistingTags = ["Condiment", "Soft Drink"];

    private static (ChargingChatClient charging, T service) Wire<T>(
        Func<Microsoft.Extensions.AI.IChatClient, T> build, params string[] replies)
    {
        var charging = new ChargingChatClient(
            FakeChatClient.Returning([.. replies.Select(Responses.Text)]));
        return (charging, build(charging));
    }

    private static AnthropicRecipeImporter Importer(Microsoft.Extensions.AI.IChatClient c) =>
        new(c, Options.Create(new LlmOptions()), NullLogger<AnthropicRecipeImporter>.Instance);

    private static AnthropicRecipeAdvisor RecipeAdvisor(Microsoft.Extensions.AI.IChatClient c) =>
        new(c, Options.Create(new LlmOptions()), NullLogger<AnthropicRecipeAdvisor>.Instance);

    // ---- the answer that says "nothing here" is still an answer -----------------------------------

    [Fact]
    public async Task A_photo_with_no_recipe_in_it_is_an_answer_the_household_pays_for()
    {
        // "found: false" is the importer's anti-hallucination floor: the model looked and said honestly
        // that there is no recipe there. That is the outcome the feature WANTS — an invented recipe is
        // the failure — so it cannot be the one the household gets its money back for.
        var (charging, importer) = Wire(Importer, """
        { "found": false, "name": null, "blurb": null, "calories_per_serving": null,
          "ingredients": [], "steps": [], "tags": [] }
        """);

        var result = await importer.ImportFromImageAsync(OneRecipePhoto);

        Assert.Null(result.Recipe);
        Assert.True(charging.Charged);
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task No_recipe_worth_suggesting_is_an_answer()
    {
        var (charging, advisor) = Wire(RecipeAdvisor, """{ "recipes": [] }""");

        Assert.Empty(await advisor.SuggestAsync("something with anchovies", [], []));
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task A_recipe_that_cannot_be_adapted_to_what_is_on_hand_is_an_answer()
    {
        var (charging, advisor) = Wire(RecipeAdvisor, """{ "recipes": [] }""");
        var recipe = new RecipeToAdapt("Paella", null, [new AdaptIngredient("Saffron", true, "1 g")], []);

        Assert.Null(await advisor.AdaptAsync(recipe, [], []));
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task NONE_from_the_recipe_tag_advisor_is_an_answer()
    {
        // ⚠️ The sentinel returns EARLY, before the parse — so the settlement has to happen above it. The
        // first version of this change settled after the parse and left every NONE refunded while its own
        // comment said the opposite.
        var (charging, advisor) = Wire(
            c => new AnthropicRecipeTagAdvisor(c, Options.Create(new LlmOptions()),
                NullLogger<AnthropicRecipeTagAdvisor>.Instance),
            "NONE");

        Assert.Empty(await advisor.SuggestAsync("Toast", ["Bread"], ExistingTags));
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task No_synonym_among_the_existing_tags_is_an_answer()
    {
        var (charging, advisor) = Wire(
            c => new AnthropicTagAdvisor(c, Options.Create(new LlmOptions()),
                NullLogger<AnthropicTagAdvisor>.Instance),
            "NONE");

        Assert.Null(await advisor.FindSynonymAsync("Snack", ExistingTags));
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task Nothing_substitutes_for_this_product_is_an_answer()
    {
        var (charging, advisor) = Wire(
            c => new AnthropicProductSubstituteAdvisor(c, Options.Create(new LlmOptions()),
                NullLogger<AnthropicProductSubstituteAdvisor>.Instance),
            "NONE");

        Assert.Empty(await advisor.SuggestAsync("Saffron", "Pantry"));
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task No_alternative_to_this_ingredient_is_an_answer()
    {
        var (charging, advisor) = Wire(
            c => new AnthropicIngredientAlternativesAdvisor(c, Options.Create(new LlmOptions()),
                NullLogger<AnthropicIngredientAlternativesAdvisor>.Instance),
            "NONE");

        Assert.Empty(await advisor.SuggestAsync("Saffron"));
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task A_receipt_that_read_clean_with_no_lines_on_it_still_read()
    {
        var (charging, extractor) = Wire(
            c => new AnthropicReceiptExtractor(c, Options.Create(new LlmOptions()),
                NullLogger<AnthropicReceiptExtractor>.Instance),
            """{ "merchant": "Walmart", "purchase_date": "2026-06-15", "lines": [] }""");

        var result = await extractor.ExtractAsync(OneReceipt);

        Assert.True(result.Success);
        Assert.Empty(result.Receipt!.Lines);
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task An_empty_shelf_is_a_true_census_of_an_empty_shelf()
    {
        var (charging, reader) = Wire(
            c => new AnthropicShelfCensusReader(c, Options.Create(new LlmOptions()),
                NullLogger<AnthropicShelfCensusReader>.Instance),
            """{ "items": [] }""");

        var result = await reader.ReadAsync(OnePhoto);

        Assert.True(result.Success);
        Assert.Empty(result.Items);
        Assert.Null(charging.RefundedFor);
    }

    // ---- and the act that got nothing back still comes back ---------------------------------------

    [Fact]
    public async Task A_reply_that_cannot_be_read_at_all_is_refunded()
    {
        // Two scripted replies because the importer validates and retries once; both are unreadable, so
        // the act ends having produced nothing. This is the side of the rule the decision did NOT move.
        var (charging, importer) = Wire(Importer, "not json", "still not json");

        var result = await importer.ImportFromImageAsync(OneRecipePhoto);

        Assert.False(result.Success);
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }

    [Fact]
    public async Task A_provider_that_says_nothing_at_all_is_refunded()
    {
        // Distinct from "NONE": the model produced no text, which is not an answer to anything. All four
        // prose advisors draw the line in the same place, and this is the one that proves the line exists.
        var (charging, advisor) = Wire(
            c => new AnthropicProductSubstituteAdvisor(c, Options.Create(new LlmOptions()),
                NullLogger<AnthropicProductSubstituteAdvisor>.Instance),
            "   ");

        Assert.Empty(await advisor.SuggestAsync("Saffron", "Pantry"));
        Assert.Equal(0, charging.RefundedFor);
    }
}
