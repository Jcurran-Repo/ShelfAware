using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
