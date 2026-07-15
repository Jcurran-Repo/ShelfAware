using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;

namespace ShelfAware.Llm.Tests;

public class PantryChatTests
{
    private static AnthropicPantryChat Chat(FakeChatClient client, FakePantryStore store) =>
        new(client, Options.Create(new LlmOptions()), store, NullLogger<AnthropicPantryChat>.Instance);

    private static Product P(int id, string name, Category category = Category.Other) =>
        new() { Id = id, Name = name, Category = category };

    private static AnthropicPantryChat ChatWithRecipe(
        FakeChatClient client, FakePantryStore store, IRecipeAdvisor advisor, IAppSettings? settings = null) =>
        new(client, Options.Create(new LlmOptions()), store, NullLogger<AnthropicPantryChat>.Instance,
            recipeAdvisor: advisor, settings: settings);

    // One on-hand main (steak), one missing main (zucchini), one missing seasoning (soy sauce).
    private static RecipeSuggestion HibachiRecipe() => new("Steak Hibachi", "Teppanyaki at home.",
        [
            new SuggestedIngredient("Steak", true, "Ribeye Steak"),
            new SuggestedIngredient("Zucchini", true, null),
            new SuggestedIngredient("Soy sauce", false, null),
        ],
        ["Sear the steak.", "Grill the zucchini."]);

    private sealed class StubRecipeAdvisor(RecipeSuggestion suggestion) : IRecipeAdvisor
    {
        public Task<IReadOnlyList<RecipeSuggestion>> SuggestAsync(
            string request, IReadOnlyList<string> onHand, IReadOnlyList<string> excludedFoods, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecipeSuggestion>>([suggestion]);

        public Task<RecipeSuggestion?> AdaptAsync(
            RecipeToAdapt recipe, IReadOnlyList<PantryProduct> onHand, IReadOnlyList<string> excludedFoods,
            string? preference = null, CancellationToken ct = default) => Task.FromResult<RecipeSuggestion?>(null);
    }

    private sealed class StubSettings(string? recipeAddConfirm) : IAppSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(key == SettingKeys.RecipeAddConfirm ? recipeAddConfirm : null);
        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Blank_input_is_rejected_without_calling_the_model()
    {
        var client = FakeChatClient.Returning(Responses.Text("unused"));
        var result = await Chat(client, new FakePantryStore()).HandleAsync("   ");

        Assert.False(result.Success);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Records_a_signal_from_a_tool_call()
    {
        var store = new FakePantryStore(P(1, "Coffee", Category.Beverage));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("record_signal", ("product_name", "coffee"), ("kind", "OutNow"))),
            () => Responses.Text("Marked coffee out."));

        var result = await Chat(client, store).HandleAsync("we're out of coffee");

        Assert.True(result.Success);
        Assert.Contains((1, SignalKind.OutNow), store.Signals);
        Assert.Equal(2, client.CallCount); // tool turn, then the final reply
    }

    [Fact]
    public async Task Handles_several_tool_calls_in_one_turn()
    {
        var store = new FakePantryStore(P(1, "Coffee", Category.Beverage), P(2, "Dog Food", Category.PetCare));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(
                Responses.Call("record_signal", ("product_name", "dog food"), ("kind", "OutNow")),
                Responses.Call("record_signal", ("product_name", "coffee"), ("kind", "RunningLow"))),
            () => Responses.Text("Done."));

        var result = await Chat(client, store).HandleAsync("out of dog food, low on coffee");

        Assert.True(result.Success);
        Assert.Contains((2, SignalKind.OutNow), store.Signals);
        Assert.Contains((1, SignalKind.RunningLow), store.Signals);
    }

    [Fact]
    public async Task Reads_tool_arguments_that_arrive_as_json_elements()
    {
        // The production path deserializes arguments as JsonElement — exercise that branch.
        var store = new FakePantryStore(P(1, "Coffee", Category.Beverage));
        var call = new FunctionCallContent("c1", "record_signal", new Dictionary<string, object?>
        {
            ["product_name"] = JsonSerializer.SerializeToElement("coffee"),
            ["kind"] = JsonSerializer.SerializeToElement("OutNow"),
        });
        var client = new FakeChatClient(
            () => new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])),
            () => Responses.Text("ok"));

        await Chat(client, store).HandleAsync("out of coffee");

        Assert.Contains((1, SignalKind.OutNow), store.Signals);
    }

    [Fact]
    public async Task Logs_a_purchase_with_quantity()
    {
        var store = new FakePantryStore(P(5, "Whole Milk", Category.Dairy));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("add_purchase", ("product_name", "whole milk"), ("quantity", 2))),
            () => Responses.Text("Logged."));

        await Chat(client, store).HandleAsync("bought 2 milk");

        var purchase = Assert.Single(store.Purchases);
        Assert.Equal(5, purchase.ProductId);
        Assert.Equal(2m, purchase.Qty);
    }

    [Fact]
    public async Task Creates_a_product_with_tags()
    {
        var store = new FakePantryStore();
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("create_product",
                ("name", "Wagyu Beef Tips"), ("category", "Meat"), ("tags", new[] { "Beef", "Protein" }))),
            () => Responses.Text("Created."));

        await Chat(client, store).HandleAsync("add a new product, wagyu beef tips");

        var created = Assert.Single(store.Products);
        Assert.Equal("Wagyu Beef Tips", created.Name);
        Assert.Equal(Category.Meat, created.Category);
        Assert.Equal(new[] { "Beef", "Protein" }, created.Tags.Select(t => t.Value));
    }

    [Fact]
    public async Task Explicit_substitutes_are_saved_verbatim_without_the_advisor()
    {
        // "Add it as a variant for ground beef" — the user SAID the phrase, so it is saved exactly as
        // stated. No substitute advisor is wired here on purpose: the explicit path must not need one
        // (the original bug — the tool could only auto-generate, so the user's phrase was ignored).
        var store = new FakePantryStore(P(7, "Wagyu Beef Tips", Category.Meat));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("suggest_substitutes",
                ("product_name", "wagyu beef tips"), ("substitutes", new[] { "ground beef" }))),
            () => Responses.Text("Saved."));

        var result = await Chat(client, store).HandleAsync("add wagyu beef tips as a variant for ground beef");

        Assert.True(result.Success);
        Assert.Contains((7, "ground beef"), store.Substitutes);
    }

    [Fact]
    public async Task Tags_an_existing_product()
    {
        var store = new FakePantryStore(P(7, "Wagyu Beef Tips", Category.Meat));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("add_tags",
                ("product_name", "wagyu"), ("tags", new[] { "Beef" }))),
            () => Responses.Text("Tagged."));

        await Chat(client, store).HandleAsync("tag the wagyu as beef");

        Assert.Contains(store.Products.Single().Tags, t => t.Value == "Beef");
    }

    [Fact]
    public async Task A_purchase_on_an_untracked_product_resumes_tracking_and_tells_the_model()
    {
        // "Bought milk" by voice must end an "ignore for a while" (grocery-list Untrack) exactly like a
        // receipt confirm does — and the tool result must say so, so the assistant can tell the user.
        var milk = P(5, "Whole Milk", Category.Dairy);
        milk.IsTracked = false;
        var store = new FakePantryStore(milk);
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("add_purchase", ("product_name", "whole milk"))),
            () => Responses.Text("Logged — and I'm tracking milk again."));

        await Chat(client, store).HandleAsync("bought milk");

        Assert.True(milk.IsTracked);
        var toolResult = client.ReceivedMessages[1]
            .Single(m => m.Role == ChatRole.Tool)
            .Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.Contains("resumed tracking", toolResult);
    }

    [Fact]
    public async Task Add_recipe_to_list_adds_only_missing_mains_and_never_signals()
    {
        var store = new FakePantryStore(P(1, "Ribeye Steak"));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("add_recipe_to_list", ("recipe", "steak hibachi"))),
            () => Responses.Text("Added zucchini to your list."));

        var result = await ChatWithRecipe(client, store, new StubRecipeAdvisor(HibachiRecipe()), new StubSettings("Auto"))
            .HandleAsync("add everything for steak hibachi");

        Assert.True(result.Success);
        Assert.Contains("Zucchini", store.GroceryExtras);        // missing main → added
        Assert.DoesNotContain("Steak", store.GroceryExtras);     // already on hand → skipped
        Assert.DoesNotContain("Soy sauce", store.GroceryExtras); // seasoning, not requested
        Assert.Empty(store.Signals);                             // adding to the list is NOT an "I'm out"
    }

    [Fact]
    public async Task Add_recipe_to_list_includes_seasonings_when_asked()
    {
        var store = new FakePantryStore(P(1, "Ribeye Steak"));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("add_recipe_to_list",
                ("recipe", "steak hibachi"), ("include_seasonings", true))),
            () => Responses.Text("Added zucchini and soy sauce."));

        await ChatWithRecipe(client, store, new StubRecipeAdvisor(HibachiRecipe()), new StubSettings("Auto"))
            .HandleAsync("add everything for steak hibachi, seasonings too");

        Assert.Contains("Zucchini", store.GroceryExtras);
        Assert.Contains("Soy sauce", store.GroceryExtras);
    }

    [Fact]
    public async Task Add_recipe_to_list_confirms_first_then_adds_on_agreement()
    {
        var store = new FakePantryStore(P(1, "Ribeye Steak"));
        var advisor = new StubRecipeAdvisor(HibachiRecipe());

        // Default (Confirm) mode: the first call PROPOSES and adds nothing yet.
        var propose = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("add_recipe_to_list", ("recipe", "steak hibachi"))),
            () => Responses.Text("For Steak Hibachi you'd need zucchini. Add it?"));
        await ChatWithRecipe(propose, store, advisor, new StubSettings(null)).HandleAsync("add everything for steak hibachi");
        Assert.Empty(store.GroceryExtras);

        // The user agrees → confirmed=true actually adds.
        var confirm = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("add_recipe_to_list", ("recipe", "steak hibachi"), ("confirmed", true))),
            () => Responses.Text("Added zucchini."));
        await ChatWithRecipe(confirm, store, advisor, new StubSettings(null)).HandleAsync("yes please");
        Assert.Contains("Zucchini", store.GroceryExtras);
    }

    [Fact]
    public async Task Untracks_a_product()
    {
        var store = new FakePantryStore(P(9, "Soda", Category.Beverage));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("set_tracking", ("product_name", "soda"), ("tracked", false))),
            () => Responses.Text("Stopped tracking soda."));

        await Chat(client, store).HandleAsync("stop tracking soda");

        Assert.Contains((9, false), store.Tracking);
    }

    [Fact]
    public async Task Creates_a_new_product()
    {
        var store = new FakePantryStore();
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("create_product", ("name", "Olive Oil"), ("category", "Pantry"))),
            () => Responses.Text("Created."));

        await Chat(client, store).HandleAsync("add olive oil");

        Assert.Contains(("Olive Oil", Category.Pantry), store.Created);
    }

    [Fact]
    public async Task Creating_a_near_duplicate_product_is_refused_and_names_the_existing_one()
    {
        var store = new FakePantryStore(P(4, "Pedigree Dog Food", Category.PetCare));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("create_product", ("name", "Dog Food"), ("category", "PetCare"))),
            () => Responses.Text("You already have Pedigree Dog Food."));

        await Chat(client, store).HandleAsync("add dog food as a product");

        Assert.Empty(store.Created); // "Dog Food" ⊂ "Pedigree Dog Food" — a twin would split its history
        var toolResult = client.ReceivedMessages[1]
            .Single(m => m.Role == ChatRole.Tool)
            .Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.Contains("Pedigree Dog Food", toolResult);
        Assert.Contains("already exists", toolResult);
        Assert.Contains("confirmed_distinct", toolResult); // the refusal must name the escape hatch
    }

    [Fact]
    public async Task A_user_confirmed_distinct_product_is_created_despite_the_fuzzy_match()
    {
        // The chat mirror of the page's "Add anyway": once the user says it's genuinely different,
        // the fuzzy guard must not make the name permanently uncreatable by voice.
        var store = new FakePantryStore(P(4, "Pedigree Dog Food", Category.PetCare));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("create_product",
                ("name", "Dog Food"), ("category", "PetCare"), ("confirmed_distinct", true))),
            () => Responses.Text("Created Dog Food."));

        await Chat(client, store).HandleAsync("yes it's a different product, create it");

        Assert.Contains(("Dog Food", Category.PetCare), store.Created);
    }

    [Fact]
    public async Task An_unmatched_product_changes_nothing()
    {
        var store = new FakePantryStore(P(1, "Coffee", Category.Beverage));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("record_signal", ("product_name", "quinoa"), ("kind", "OutNow"))),
            () => Responses.Text("I couldn't find quinoa."));

        var result = await Chat(client, store).HandleAsync("out of quinoa");

        Assert.Empty(store.Signals);   // no fuzzy match → nothing recorded
        Assert.True(result.Success);   // but it still replies gracefully
    }

    [Fact]
    public async Task A_model_failure_returns_a_friendly_error()
    {
        var client = new FakeChatClient(() => throw new HttpRequestException("boom"));

        var result = await Chat(client, new FakePantryStore()).HandleAsync("hello");

        Assert.False(result.Success);
        Assert.Contains("couldn't reach", result.Reply);
    }

    [Fact]
    public async Task A_tool_that_throws_is_reported_back_to_the_model_not_escaped()
    {
        // A mutating tool (add_purchase) can throw at the DB layer. The loop must feed that back as an
        // error tool-result so the chat still returns a reply — instead of the exception escaping
        // HandleAsync and blanking the dashboard box / push-to-talk, which don't wrap the call.
        var store = new ThrowingPantryStore(P(1, "Coffee", Category.Beverage));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("add_purchase", ("product_name", "coffee"))),
            () => Responses.Text("Sorry — I couldn't log that just now."));
        var chat = new AnthropicPantryChat(client, Options.Create(new LlmOptions()), store,
            NullLogger<AnthropicPantryChat>.Instance);

        var result = await chat.HandleAsync("i bought coffee"); // must not throw

        Assert.True(result.Success);       // the loop recovered and produced a reply
        Assert.Equal(2, client.CallCount); // tool turn, then the model saw the error and replied
        // The failed tool's result must reach the model so it can explain rather than crash.
        var toolResult = client.ReceivedMessages[1]
            .Single(m => m.Role == ChatRole.Tool)
            .Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.Contains("add_purchase", toolResult);
    }

    [Fact]
    public async Task Replays_conversation_history_so_follow_ups_have_context()
    {
        // Turn 1 ("what am I low on?") already happened; now the follow-up references it. The prior
        // exchange must be replayed into the model's context so "the first two" is resolvable.
        var store = new FakePantryStore(P(1, "Olive Oil", Category.Pantry), P(2, "Coffee", Category.Beverage));
        var history = new[] { new ChatTurn("what am I low on?", "You're low on olive oil and coffee.") };
        var client = new FakeChatClient(
            () => Responses.ToolCalls(
                Responses.Call("add_purchase", ("product_name", "olive oil")),
                Responses.Call("add_purchase", ("product_name", "coffee"))),
            () => Responses.Text("Added olive oil and coffee."));

        var result = await Chat(client, store).HandleAsync("add the first two", history);

        Assert.True(result.Success);
        Assert.Equal(2, store.Purchases.Count);

        // First model call of this turn must carry: system, then the prior turn, then the new user text.
        var convo = client.ReceivedMessages[0]
            .Where(m => m.Role != ChatRole.System)
            .Select(m => (m.Role, m.Text))
            .ToList();
        Assert.Equal((ChatRole.User, "what am I low on?"), convo[0]);
        Assert.Equal((ChatRole.Assistant, "You're low on olive oil and coffee."), convo[1]);
        Assert.Equal((ChatRole.User, "add the first two"), convo[2]);
    }

    [Fact]
    public async Task Import_receipts_tool_invokes_the_importer_and_reports()
    {
        var importer = new FakeReceiptImporter(new ShelfAware.Core.Ingest.ImportSummary(true, 2, 7, 1, 0, 0));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("import_receipts")),
            () => Responses.Text("Imported your receipts."));
        var chat = new AnthropicPantryChat(client, Options.Create(new LlmOptions()), new FakePantryStore(),
            NullLogger<AnthropicPantryChat>.Instance, importer);

        var result = await chat.HandleAsync("import my receipts");

        Assert.True(result.Success);
        Assert.Equal(1, importer.Calls);
        Assert.Contains("imported 2 receipt(s)", result.Actions);
    }

    [Fact]
    public async Task Suggest_substitutes_tool_generates_and_saves_them()
    {
        var store = new FakePantryStore(P(50, "Chicken Breast Tenderloins", Category.Meat));
        var advisor = new FakeSubstituteAdvisor("chicken breast", "chicken cutlet");
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("suggest_substitutes", ("product_name", "chicken tenderloins"))),
            () => Responses.Text("Added substitutes for Chicken Breast Tenderloins."));
        var chat = new AnthropicPantryChat(client, Options.Create(new LlmOptions()), store,
            NullLogger<AnthropicPantryChat>.Instance, importer: null, substituteAdvisor: advisor);

        var result = await chat.HandleAsync("generate substitutes for the chicken tenderloins");

        Assert.True(result.Success);
        Assert.Equal(1, advisor.Calls);
        Assert.Contains((50, "chicken breast"), store.Substitutes);
        Assert.Contains((50, "chicken cutlet"), store.Substitutes);
    }

    [Fact]
    public async Task Adapt_recipe_tool_adapts_the_resolved_recipe_and_navigates()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Spaghetti Carbonara", HasSteps: true));
        var adapter = new FakeRecipeAdapter(new ShelfAware.Core.Recipes.AdaptResult(true, "Saved a version using what you have.", 42));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("adapt_recipe", ("recipe_name", "carbonara"))),
            () => Responses.Text("Adapted it."));
        var chat = new AnthropicPantryChat(client, Options.Create(new LlmOptions()), store,
            NullLogger<AnthropicPantryChat>.Instance, recipeAdapter: adapter);

        var result = await chat.HandleAsync("adapt the carbonara to what I have");

        Assert.True(result.Success);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(7, adapter.LastRecipeId);
        Assert.Equal("/recipes", result.NavigateTo);
        Assert.False(result.HandsOff); // shows the variant but keeps listening
    }

    // The safety net under the cook-along's plain-code grammar. That grammar matches whole utterances, so
    // a cough, a stutter or an unlisted phrasing ("up next") misses it and lands here — where it used to
    // be ANSWERED instead of obeyed. With this the miss costs a model call instead of the wrong outcome,
    // which is what lets the grammar stay conservative rather than trying to list every way to say "next".
    [Fact]
    public async Task Go_to_step_carries_a_step_target_out()
    {
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("go_to_step", ("step", 4))),
            () => Responses.Text("Moving to step 4."));

        var result = await Chat(client, new FakePantryStore()).HandleAsync("up next");

        Assert.True(result.Success);
        Assert.Equal(4, result.StepTarget);
    }

    [Fact]
    public async Task Go_to_step_zero_means_start_the_recipe_over()
    {
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("go_to_step", ("step", 0))),
            () => Responses.Text("Starting over."));

        var result = await Chat(client, new FakePantryStore()).HandleAsync("take it from the top again");

        Assert.Equal(0, result.StepTarget);
    }

    // Only the reader knows how long its recipe is, so an out-of-range step rides out and IT decides.
    // Guessing here would mean duplicating the recipe's length into the chat layer.
    [Fact]
    public async Task Go_to_step_does_not_range_check_what_it_cannot_know()
    {
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("go_to_step", ("step", 99))),
            () => Responses.Text("Moving."));

        Assert.Equal(99, (await Chat(client, new FakePantryStore()).HandleAsync("step 99")).StepTarget);
    }

    [Fact]
    public async Task Go_to_step_rejects_a_missing_or_negative_step()
    {
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("go_to_step", ("step", -1))),
            () => Responses.Text("I couldn't do that."));

        Assert.Null((await Chat(client, new FakePantryStore()).HandleAsync("go back a lot")).StepTarget);
    }

    // Ordinary chat must never carry a step target — everything that isn't a cook-along ignores it, but a
    // stray one would silently yank a reader that happened to be open.
    [Fact]
    public async Task An_ordinary_reply_carries_no_step_target()
    {
        var client = new FakeChatClient(() => Responses.Text("You have two litres of milk."));

        Assert.Null((await Chat(client, new FakePantryStore()).HandleAsync("how much milk do I have")).StepTarget);
    }

    [Fact]
    public async Task Open_page_carries_a_navigation_target_out()
    {
        var store = new FakePantryStore();
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("open_page", ("page", "grocery_list"))),
            () => Responses.Text("Opening the grocery list."));

        var result = await Chat(client, store).HandleAsync("show me the grocery list");

        Assert.True(result.Success);
        Assert.Equal("/list", result.NavigateTo);
        Assert.False(result.HandsOff); // plain navigation — the agent keeps listening to chain commands
    }

    [Fact]
    public async Task Open_product_page_resolves_the_product_fuzzily()
    {
        var store = new FakePantryStore(P(7, "Whole Milk", Category.Dairy));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("open_page", ("page", "product"), ("product_name", "milk"))),
            () => Responses.Text("Opening Whole Milk."));

        var result = await Chat(client, store).HandleAsync("show me the milk page");

        Assert.Equal("/product/7", result.NavigateTo);
        Assert.False(result.HandsOff);
    }

    [Fact]
    public async Task Open_recipes_for_a_product_navigates_to_the_filtered_view()
    {
        // The middle link of the hands-free chain: product -> recipes that use it -> read one.
        var store = new FakePantryStore(P(7, "Chicken Breast", Category.Meat));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("open_page", ("page", "recipes"), ("product_name", "chicken"))),
            () => Responses.Text("Showing recipes that use Chicken Breast."));

        var result = await Chat(client, store).HandleAsync("show me recipes that use the chicken");

        Assert.Equal("/recipes?uses=7", result.NavigateTo);
        Assert.False(result.HandsOff); // still just navigation — keep listening
    }

    [Fact]
    public async Task Screen_context_is_given_to_the_model_so_positional_references_resolve()
    {
        // "the second one" is meaningless without knowing what's on screen. The agent passes the on-screen
        // list; the model maps position -> name and calls read_recipe with it. We can only own the
        // plumbing (the list reaches the prompt); the position->name mapping is the model's job, faked here.
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(1, "Chicken Parmesan", HasSteps: true));
        store.Recipes.Add(new RecipeRef(2, "Chicken Tikka Masala", HasSteps: true));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("read_recipe", ("recipe_name", "Chicken Tikka Masala"))),
            () => Responses.Text("Reading Chicken Tikka Masala."));
        var screen = "The user is viewing recipes that use Chicken. Recipes listed, in display order:\n1. Chicken Parmesan\n2. Chicken Tikka Masala";

        var result = await Chat(client, store).HandleAsync("read me the second one", history: null, screenContext: screen);

        Assert.Equal("/recipes?read=2", result.NavigateTo);
        Assert.True(result.HandsOff);
        // The model could only resolve "the second one" because the on-screen list rode into its prompt.
        var systemText = client.ReceivedMessages[0].Single(m => m.Role == ChatRole.System).Text;
        Assert.Contains("Chicken Tikka Masala", systemText);
        Assert.Contains("in display order", systemText);
    }

    [Fact]
    public async Task Read_recipe_resolves_a_close_name_and_navigates_to_the_reader()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(12, "Spaghetti Carbonara", HasSteps: true));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("read_recipe", ("recipe_name", "carbonara"))),
            () => Responses.Text("Opening Spaghetti Carbonara and reading it aloud."));

        var result = await Chat(client, store).HandleAsync("read me the carbonara recipe");

        Assert.True(result.Success);
        Assert.Equal("/recipes?read=12", result.NavigateTo);
        Assert.Contains("reading Spaghetti Carbonara", result.Actions);
        // read_recipe is a hand-off: the reader produces its own audio, so the listening agent stands down.
        Assert.True(result.HandsOff);
    }

    [Fact]
    public async Task Read_recipe_resolves_a_descriptive_reference_by_token_containment()
    {
        // "the chicken and potatoes recipe" is neither exact nor a substring of the saved name —
        // token containment (the eval harness's matcher) must land it deterministically.
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(5, "Pan-Seared Chicken with Roasted Potatoes", HasSteps: true));
        store.Recipes.Add(new RecipeRef(6, "Beef Stroganoff", HasSteps: true));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("read_recipe", ("recipe_name", "the chicken and potatoes recipe"))),
            () => Responses.Text("Opening it."));

        var result = await Chat(client, store).HandleAsync("read me the chicken and potatoes recipe");

        Assert.Equal("/recipes?read=5", result.NavigateTo);
    }

    [Fact]
    public async Task Read_recipe_with_no_match_reports_the_saved_names_and_does_not_navigate()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(12, "Spaghetti Carbonara", HasSteps: true));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("read_recipe", ("recipe_name", "beef wellington"))),
            () => Responses.Text("I don't have that one saved — you do have Spaghetti Carbonara."));

        var result = await Chat(client, store).HandleAsync("read me the beef wellington recipe");

        Assert.Null(result.NavigateTo);
        // The tool result fed back to the model must list what IS saved, so it can self-correct.
        var toolResult = client.ReceivedMessages[1]
            .Single(m => m.Role == ChatRole.Tool)
            .Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.Contains("Spaghetti Carbonara", toolResult);
    }

    [Fact]
    public async Task Read_recipe_without_steps_refuses_to_navigate()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(3, "Caprese Salad", HasSteps: false));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("read_recipe", ("recipe_name", "caprese salad"))),
            () => Responses.Text("That one has no steps saved."));

        var result = await Chat(client, store).HandleAsync("read me the caprese recipe");

        Assert.Null(result.NavigateTo);
    }

    [Fact]
    public async Task Read_recipe_by_position_indexes_the_display_ordered_list()
    {
        // No on-screen list needed: "read the second recipe" works from ANY page because the store's
        // list is in Recipes-page display order and the tool takes a 1-based position into it.
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(9, "Skillet Beef Tacos", HasSteps: true));
        store.Recipes.Add(new RecipeRef(4, "Spaghetti Carbonara", HasSteps: true));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("read_recipe", ("position", 2))),
            () => Responses.Text("Opening Spaghetti Carbonara and reading it aloud."));

        var result = await Chat(client, store).HandleAsync("read me the second recipe");

        Assert.Equal("/recipes?read=4", result.NavigateTo);
        Assert.True(result.HandsOff);
    }

    [Fact]
    public async Task Read_recipe_position_out_of_range_reports_the_list_and_does_not_navigate()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(9, "Skillet Beef Tacos", HasSteps: true));
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("read_recipe", ("position", 3))),
            () => Responses.Text("There's only one saved recipe — Skillet Beef Tacos."));

        var result = await Chat(client, store).HandleAsync("read the third recipe");

        Assert.Null(result.NavigateTo);
        // The tool result must list what IS saved (in order), so the model can self-correct.
        var toolResult = client.ReceivedMessages[1]
            .Single(m => m.Role == ChatRole.Tool)
            .Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.Contains("Skillet Beef Tacos", toolResult);
    }

    [Fact]
    public async Task Stops_after_the_turn_limit()
    {
        // The model keeps calling a tool and never gives a final answer → the loop must bail out.
        var store = new FakePantryStore(P(1, "Coffee", Category.Beverage));
        var loops = Enumerable.Repeat<Func<ChatResponse>>(
            () => Responses.ToolCalls(Responses.Call("query_status")), 10).ToArray();
        var client = new FakeChatClient(loops);

        var result = await Chat(client, store).HandleAsync("status?");

        Assert.True(result.Success);
        Assert.Equal(5, client.CallCount); // MaxTurns
    }
}
