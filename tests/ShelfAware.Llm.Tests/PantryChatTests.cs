using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;

namespace ShelfAware.Llm.Tests;

public class PantryChatTests
{
    private static AnthropicPantryChat Chat(FakeChatClient client, FakePantryStore store) =>
        new(client, Options.Create(new LlmOptions()), store, NullLogger<AnthropicPantryChat>.Instance);

    private static Product P(int id, string name, Category category = Category.Other) =>
        new() { Id = id, Name = name, Category = category };

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
    public async Task Open_page_carries_a_navigation_target_out()
    {
        var store = new FakePantryStore();
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("open_page", ("page", "grocery_list"))),
            () => Responses.Text("Opening the grocery list."));

        var result = await Chat(client, store).HandleAsync("show me the grocery list");

        Assert.True(result.Success);
        Assert.Equal("/list", result.NavigateTo);
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
