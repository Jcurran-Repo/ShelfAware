using Microsoft.Extensions.AI;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// A scripted <see cref="IChatClient"/>: returns queued responses in order (or throws), and records
/// what it was called with. Lets us drive the tool-calling loop and the extractor's retry logic with
/// no live API — the whole point of putting the AI services behind IChatClient.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<Func<ChatResponse>> _script;
    public int CallCount { get; private set; }
    public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = [];

    public FakeChatClient(params Func<ChatResponse>[] script) => _script = new(script);

    /// <summary>Queue plain responses to hand back in order.</summary>
    public static FakeChatClient Returning(params ChatResponse[] responses) =>
        new([.. responses.Select(r => (Func<ChatResponse>)(() => r))]);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        ReceivedMessages.Add([.. messages]);
        if (_script.Count == 0) throw new InvalidOperationException("FakeChatClient ran out of scripted responses.");
        return Task.FromResult(_script.Dequeue()());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Terse builders for the canned responses the fake hands back.</summary>
internal static class Responses
{
    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse ToolCalls(params FunctionCallContent[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, [.. calls.Cast<AIContent>()]));

    public static FunctionCallContent Call(string name, params (string Key, object? Value)[] args) =>
        new(Guid.NewGuid().ToString("N"), name, args.ToDictionary(a => a.Key, a => a.Value));
}

/// <summary>In-memory <see cref="IPantryStore"/> that records the mutations the chat tools apply.</summary>
internal sealed class FakePantryStore : IPantryStore
{
    public List<Product> Products { get; }
    public List<(int ProductId, SignalKind Kind)> Signals { get; } = [];
    public List<(int ProductId, DateOnly Date, decimal Qty)> Purchases { get; } = [];
    public List<(int ProductId, bool Tracked)> Tracking { get; } = [];
    public List<(string Name, Category Category)> Created { get; } = [];

    public FakePantryStore(params Product[] products) => Products = [.. products];

    public Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Product>>(Products);

    public Task<int> CreateProductAsync(string name, Category category, CancellationToken cancellationToken = default)
    {
        Created.Add((name, category));
        var product = new Product { Id = 1000 + Products.Count, Name = name, Category = category };
        Products.Add(product);
        return Task.FromResult(product.Id);
    }

    public Task AddPurchaseAsync(int productId, DateOnly purchasedAt, decimal quantity, CancellationToken cancellationToken = default)
    {
        Purchases.Add((productId, purchasedAt, quantity));
        return Task.CompletedTask;
    }

    public Task RecordSignalAsync(int productId, SignalKind kind, CancellationToken cancellationToken = default)
    {
        Signals.Add((productId, kind));
        return Task.CompletedTask;
    }

    public Task SetTrackingAsync(int productId, bool tracked, CancellationToken cancellationToken = default)
    {
        Tracking.Add((productId, tracked));
        return Task.CompletedTask;
    }
}
