using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Census;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Chat;

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
/// <para>⚠️ Every case asserts <c>Charged</c> as well as what came back. Without it they pass vacuously:
/// <c>RefundedFor</c> is written only from inside the settlement, so "nothing was given back" and "nothing
/// was ever charged" look identical — delete a service's <c>Begin</c> line altogether and the test would
/// still be green. A review found eight of eleven in that state.</para>
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
    public async Task Suggestions_that_came_back_empty_are_refunded()
    {
        // ⚠️ The twin of the adapt case below, and it survived the commit that fixed that one —
        // eight lines away, on the identical fake reply, under a name asserting the opposite.
        // recipe-suggest-system.txt rule 2 says "Suggest 1-3 recipe ideas" and never offers the model a
        // way to decline, so an empty array is the model failing, not answering. Recipes.razor turns it
        // into "No ideas came back — try rephrasing" beside a button that charges again.
        var (charging, advisor) = Wire(RecipeAdvisor, """{ "recipes": [] }""");

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<RecipeSuggestion>>(
            await advisor.SuggestAsync("something with anchovies", [], [])));
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }

    [Fact]
    public async Task Suggestions_with_no_name_are_refunded_like_an_adaptation_with_no_name()
    {
        // ⚠️ The SAME question as the adapt case, through the same predicate — and it was not, for one
        // commit. RecipeReply.Landed means present AND NAMED; SuggestAsync eight lines above it checked
        // only `Count == 0`, i.e. present. RecipeJson.Parse keeps an unnamed entry (`name` falls back to
        // ""), so this exact reply was refunded by one method and charged in full by the other, while
        // Recipes.razor drew a card with a blank title and the chat said "For  you'd need: …".
        //
        // The consolidated definition shipped NARROWER than the sites around it, which is the failure
        // this arc has now repeated in five consecutive rounds — see CLAUDE.md item 41.
        var (charging, advisor) = Wire(RecipeAdvisor, """
        { "recipes": [ { "name": "  ", "blurb": "", "ingredients": [], "steps": [] } ] }
        """);

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<RecipeSuggestion>>(
            await advisor.SuggestAsync("anything", [], [])));
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }

    [Fact]
    public async Task A_nameless_suggestion_is_dropped_but_its_named_siblings_are_delivered_and_paid_for()
    {
        // The other half: filtering to what landed must not throw away a usable batch. One good idea in
        // the reply is an answer, and §4.w pays for an answer.
        var (charging, advisor) = Wire(RecipeAdvisor, """
        { "recipes": [ { "name": "", "blurb": "", "ingredients": [], "steps": [] },
                       { "name": "Chickpea Tacos", "blurb": "", "ingredients": [], "steps": [] } ] }
        """);

        var ideas = await advisor.SuggestAsync("anything", [], []);

        Assert.Equal(["Chickpea Tacos"], Assert.IsAssignableFrom<IReadOnlyList<RecipeSuggestion>>(ideas).Select(i => i.Name));
        Assert.True(charging.Charged);
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task A_suggestion_call_that_never_landed_is_refunded_and_says_so()
    {
        // Null is the engine saying "couldn't reach it", which is why the page can stop inferring that
        // from an escaping exception — the escape was tearing the circuit.
        var charging = new ChargingChatClient(new ThrowingChatClient(new HttpRequestException("no route")));

        Assert.Null(await RecipeAdvisor(charging).SuggestAsync("anything", [], []));
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }

    [Fact]
    public async Task An_adaptation_that_came_back_empty_is_refunded()
    {
        // ⚠️ This test used to assert the opposite, under the name "a recipe that cannot be adapted
        // to what is on hand is an answer" — and the name was the error. §4.w pays for an honest
        // "nothing here", but recipe-adapt-system.txt never offers the model that answer: rule 1 says
        // output "a SINGLE adapted recipe in the recipes array" and rule 7 says return the recipe even
        // when nothing needs swapping. So an empty array is not the model declining, it is the model
        // failing to do what was asked — and RecipeAdapter turns it into "Couldn't adapt {recipe} right
        // now.", which invited the household to press the button again and be charged again.
        //
        // The lesson is narrower than the fix: a test can encode a MEANING the contract does not carry,
        // and its name is where that goes unnoticed.
        var (charging, advisor) = Wire(RecipeAdvisor, """{ "recipes": [] }""");
        var recipe = new RecipeToAdapt("Paella", null, [new AdaptIngredient("Saffron", true, "1 g")], []);

        Assert.Null(await advisor.AdaptAsync(recipe, [], []));
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }

    [Fact]
    public async Task An_adaptation_with_no_name_is_refunded()
    {
        // The other half of the same guard: the model returned a recipe object it never named, which
        // RecipeAdapter rejects with the same retry invitation.
        const string Unnamed =
            """
            { "recipes": [ { "name": "  ", "blurb": null, "calories_per_serving": null, "servings": 2,
              "ingredients": [], "steps": [] } ] }
            """;
        var (charging, advisor) = Wire(RecipeAdvisor, Unnamed);
        var recipe = new RecipeToAdapt("Paella", null, [new AdaptIngredient("Saffron", true, "1 g")], []);

        await advisor.AdaptAsync(recipe, [], []);
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
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
        Assert.True(charging.Charged);
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
        Assert.True(charging.Charged);
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
        Assert.True(charging.Charged);
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
        Assert.True(charging.Charged);
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
        Assert.True(charging.Charged);
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
        Assert.True(charging.Charged);
        Assert.Null(charging.RefundedFor);
    }

    // ---- a chat turn settles at the WRITE, not on the way out -------------------------------------

    private static AnthropicPantryChat Chat(Microsoft.Extensions.AI.IChatClient c, FakePantryStore store) =>
        new(c, Options.Create(new LlmOptions()), store, NullLogger<AnthropicPantryChat>.Instance);

    private static Product Purchased(int id, string name) =>
        new()
        {
            Id = id, Name = name, Category = Category.Beverage,
            Purchases = [new PurchaseEvent { ProductId = id, PurchasedAt = new DateOnly(2026, 7, 10) }],
        };

    [Fact]
    public async Task A_turn_that_wrote_and_then_lost_the_provider_keeps_its_charge()
    {
        // ⚠️ The case the whole TurnWrites change is for, and it had no test until a review said so. The
        // household asked for something, it landed in the pantry, and then the provider went away. Those
        // writes are committed and visible; refunding would hand back the credits for work the household
        // can see in its own pantry. Closing the tab mid-turn does the same thing, deliberately.
        var store = new FakePantryStore(Purchased(1, "Coffee"));
        var charging = new ChargingChatClient(new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("add_purchase", ("product_name", "coffee"))),
            () => throw new HttpRequestException("the provider went away")));

        var result = await Chat(charging, store).HandleAsync("i bought coffee");

        Assert.False(result.Success);
        Assert.True(charging.Charged);
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task A_turn_that_wrote_nothing_and_lost_the_provider_is_refunded()
    {
        var charging = new ChargingChatClient(new FakeChatClient(
            () => throw new HttpRequestException("the provider went away")));

        var result = await Chat(charging, new FakePantryStore()).HandleAsync("what should I cook?");

        Assert.False(result.Success);
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }

    [Fact]
    public async Task A_turn_that_ran_out_of_steps_having_done_nothing_is_refunded()
    {
        // ⚠️ This exit settled unconditionally until a review asked what it was paying for. Every round
        // names a product that isn't there, so every tool result is validation text: nothing written,
        // nothing listed, nowhere navigated. The household reads "Stopped after several steps without
        // finishing" and, before this, paid a full chat turn for it.
        var charging = new ChargingChatClient(new FakeChatClient(
            [.. Enumerable.Repeat<Func<ChatResponse>>(
                () => Responses.ToolCalls(Responses.Call("record_signal",
                    ("product_name", "something that isn't there"), ("kind", "OutNow"))), 12)]));

        var result = await Chat(charging, new FakePantryStore()).HandleAsync("mark it out");

        Assert.True(result.Success);
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }

    [Fact]
    public async Task A_turn_that_ran_out_of_steps_having_moved_the_screen_keeps_its_charge()
    {
        // ⚠️ go_to_step moves a hands-free cook-along and sets NOTHING else — no actions line, no URL. The
        // first version of the guard beside that exit read nav.Url alone while the exit itself carried
        // three navigation facts out, so this turn moved the reader on screen and was refunded in full
        // for work the household watched happen. One NavigationTarget.Moved answers it now.
        var charging = new ChargingChatClient(new FakeChatClient(
            [.. Enumerable.Repeat<Func<ChatResponse>>(
                () => Responses.ToolCalls(Responses.Call("go_to_step", ("step", 3))), 12)]));

        // A reader IS open — that is the whole case. go_to_step is offered only when one is, and a step it
        // can reach is checked against the recipe's real length before anyone is told it moved.
        var result = await Chat(charging, new FakePantryStore())
            .HandleAsync("next step", cookAlong: new CookAlongState(8));

        Assert.True(result.Success);
        Assert.Equal(3, result.StepTarget);
        Assert.True(charging.Charged);
        Assert.Null(charging.RefundedFor);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(" . ")]
    [InlineData("!")]
    [InlineData("\u2026")]
    public async Task A_turn_that_stopped_without_saying_anything_is_refunded(string silence)
    {
        // ⚠️ The chat's final-reply exit was a FIFTH site answering "did the model say anything?" its own
        // way — unconditional, one method above the guard that exists to stop exactly that. A round with
        // no tool calls and no text is a model that stopped; the household is told "Done." when nothing
        // was done, and that is not a turn to charge for.
        //
        // ⚠️ The punctuation cases are the ones that make this a rule rather than a coincidence. With
        // whitespace alone, swapping the shared predicate back for a private `text.Length > 0` leaves the
        // whole suite green — the fifth site could drift straight back to its own answer with nothing to
        // catch it. "." and "!" and "…" are exactly the inputs the four advisors are held to one line on.
        var charging = new ChargingChatClient(FakeChatClient.Returning(Responses.Text(silence)));

        var result = await Chat(charging, new FakePantryStore()).HandleAsync("hello?");

        Assert.Equal("Done.", result.Reply);
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }

    [Theory]
    [InlineData("\U0001F44D")]
    [InlineData("\u2713")]
    [InlineData("\u2192 \U0001F389")]
    public async Task A_turn_whose_whole_answer_is_a_symbol_is_paid_for(string reply)
    {
        // ⚠️ The predicate's second version asked Any(char.IsLetterOrDigit), which reads UTF-16 code
        // units — and neither half of a surrogate pair is a letter. A model that answered "👍" had its reply
        // rendered in the chat box, spoken on the voice surfaces, and refunded in full. A tick and an arrow
        // failed the same test without needing a surrogate at all. A symbol is content; punctuation is not,
        // and the line between them is what IsAnAnswer is for.
        var charging = new ChargingChatClient(FakeChatClient.Returning(Responses.Text(reply)));

        var result = await Chat(charging, new FakePantryStore()).HandleAsync("did that work?");

        Assert.Equal(reply, result.Reply);
        Assert.True(charging.Charged);
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task An_advisor_reply_that_is_a_symbol_is_paid_for_like_any_other_answer()
    {
        // The same rune question at an advisor, so the five sites are held to one line from both ends.
        // The reply names no tag, so the dedup declines — declining on an answer is still an answer.
        var charging = new ChargingChatClient(FakeChatClient.Returning(Responses.Text("\U0001F44D")));

        var match = await new AnthropicTagAdvisor(charging, Options.Create(new LlmOptions()),
            NullLogger<AnthropicTagAdvisor>.Instance).FindSynonymAsync("Snack", ExistingTags);

        Assert.Null(match);
        Assert.True(charging.Charged);
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
    public async Task A_recipe_the_model_claims_to_have_found_and_cannot_name_is_refunded()
    {
        // ⚠️ Not the same as "found: false". The model asserted a recipe and then produced no name for it,
        // which is a reply we could not read rather than an honest answer. It used to share the no-recipe
        // branch, so it returned quietly and charged; it retries now and refunds when the retry is no
        // better. Two identical bad replies here, so the retry is spent.
        const string Contradiction = """
        { "found": true, "name": null, "blurb": null, "calories_per_serving": null,
          "ingredients": [], "steps": [], "tags": [] }
        """;
        var (charging, importer) = Wire(Importer, Contradiction, Contradiction);

        var result = await importer.ImportFromImageAsync(OneRecipePhoto);

        Assert.False(result.Success);
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }

    [Fact]
    public async Task All_four_prose_advisors_draw_the_empty_reply_line_in_the_same_place()
    {
        // ⚠️ The regression this pins is not "does one of them get it right" but "do they agree". A reply
        // that is only punctuation is no answer. Three of them used to test the raw reply and the fourth
        // tested it with trailing periods stripped, so "." refunded in one and was paid for in the other
        // three — under a doc paragraph claiming they drew the line in the same place.
        //
        // ⚠️ And "!" alongside ".", because the first shared definition drew the line at "empty once
        // periods and spaces are stripped" — a parser's convenience promoted into a billing predicate,
        // under which "." refunded and "!" was charged in full. Both are a model that said nothing.
        var opts = Options.Create(new LlmOptions());
        var refunds = new List<int?>();

        foreach (var run in new Func<ChargingChatClient, Task>[]
        {
            async c => await new AnthropicProductSubstituteAdvisor(c, opts,
                NullLogger<AnthropicProductSubstituteAdvisor>.Instance).SuggestAsync("Saffron", "Pantry"),
            async c => await new AnthropicIngredientAlternativesAdvisor(c, opts,
                NullLogger<AnthropicIngredientAlternativesAdvisor>.Instance).SuggestAsync("Saffron"),
            async c => await new AnthropicRecipeTagAdvisor(c, opts,
                NullLogger<AnthropicRecipeTagAdvisor>.Instance).SuggestAsync("Toast", ["Bread"], ExistingTags),
            async c => await new AnthropicTagAdvisor(c, opts,
                NullLogger<AnthropicTagAdvisor>.Instance).FindSynonymAsync("Snack", ExistingTags),
        })
        {
            foreach (var silence in new[] { " . ", "!", "\u2026", "" })
            {
                var charging = new ChargingChatClient(FakeChatClient.Returning(Responses.Text(silence)));
                await run(charging);
                Assert.True(charging.Charged);
                refunds.Add(charging.RefundedFor);
            }
        }

        Assert.All(refunds, r => Assert.Equal(0, r));
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
        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }
}
