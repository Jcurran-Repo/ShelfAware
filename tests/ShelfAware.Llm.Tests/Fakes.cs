using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.AI;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Ingest;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Llm.Tests;

/// <summary>Returns canned substitute suggestions and counts calls — drives the suggest_substitutes tool.</summary>
internal sealed class FakeSubstituteAdvisor(params string[] suggestions) : IProductSubstituteAdvisor
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<string>> SuggestAsync(string productName, string category, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<string>>(suggestions);
    }
}

/// <summary>Records the recipe id it was asked to adapt and returns a canned result — drives adapt_recipe.</summary>
internal sealed class FakeRecipeAdapter(AdaptResult result) : IRecipeAdapter
{
    public int Calls { get; private set; }
    public int? LastRecipeId { get; private set; }
    public IngredientSwap? LastSwap { get; private set; }

    public Task<AdaptResult> AdaptToOnHandAsync(int recipeId, IngredientSwap? swap = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRecipeId = recipeId;
        LastSwap = swap;
        return Task.FromResult(result);
    }
}

/// <summary>Records import calls and returns a canned summary — drives the import_receipts chat tool.</summary>
internal sealed class FakeReceiptImporter(ImportSummary summary) : IReceiptImporter
{
    public int Calls { get; private set; }

    public Task<ImportSummary> ImportNewAsync(
        IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(summary);
    }
}

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
    public List<RecipeRef> Recipes { get; } = [];
    public List<(int ProductId, SignalKind Kind)> Signals { get; } = [];
    public List<(int ProductId, DateOnly Date, decimal Qty)> Purchases { get; } = [];
    public List<(int ProductId, bool Tracked)> Tracking { get; } = [];
    public List<(string Name, Category Category)> Created { get; } = [];
    public List<(int ProductId, string Value)> Substitutes { get; } = [];
    public List<string> GroceryExtras { get; } = [];
    public List<string> Excluded { get; } = [];

    public FakePantryStore(params Product[] products) => Products = [.. products];

    public Task<IReadOnlyList<RecipeRef>> GetRecipesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecipeRef>>(Recipes);

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

    public Task<IReadOnlyList<string>> AddSubstitutesAsync(int productId, IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        var have = new HashSet<string>(
            Substitutes.Where(s => s.ProductId == productId).Select(s => s.Value), StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var v in values)
        {
            if (have.Add(v)) { Substitutes.Add((productId, v)); added.Add(v); }
        }
        return Task.FromResult<IReadOnlyList<string>>(added);
    }

    public Task<IReadOnlyList<string>> GetExcludedFoodsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Excluded);

    public Task<IReadOnlyList<string>> AddGroceryExtrasAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        var have = new HashSet<string>(GroceryExtras, StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var n in names)
        {
            var t = n.Trim();
            if (t.Length > 0 && have.Add(t)) { GroceryExtras.Add(t); added.Add(t); }
        }
        return Task.FromResult<IReadOnlyList<string>>(added);
    }
}

/// <summary>An <see cref="IPantryStore"/> whose write throws — exercises the chat loop's tool-error
/// resilience (a DB failure inside a tool must come back as an error result, not escape HandleAsync).</summary>
internal sealed class ThrowingPantryStore(params Product[] products) : IPantryStore
{
    public Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Product>>(products);
    public Task AddPurchaseAsync(int productId, DateOnly purchasedAt, decimal quantity, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("simulated DB write failure");
    public Task<int> CreateProductAsync(string name, Category category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RecordSignalAsync(int productId, SignalKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetTrackingAsync(int productId, bool tracked, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<RecipeRef>> GetRecipesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> AddSubstitutesAsync(int productId, IReadOnlyList<string> values, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> GetExcludedFoodsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> AddGroceryExtrasAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/>: returns queued responses in order and records each
/// request it received (method, URI, xi-api-key header, content-type, and the buffered body). The
/// HTTP-level analogue of <see cref="FakeChatClient"/> — it lets us drive the ElevenLabs speech
/// services with no live API. The body is read eagerly in <see cref="SendAsync"/> because request
/// content is disposed once the call returns.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _script;
    public List<CapturedRequest> Requests { get; } = [];

    public FakeHttpMessageHandler(params Func<HttpResponseMessage>[] script) => _script = new(script);

    public static FakeHttpMessageHandler Returning(params HttpResponseMessage[] responses) =>
        new([.. responses.Select(r => (Func<HttpResponseMessage>)(() => r))]);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("xi-api-key", out var k) ? k.FirstOrDefault() : null,
            request.Content?.Headers.ContentType?.MediaType,
            body));
        if (_script.Count == 0) throw new InvalidOperationException("FakeHttpMessageHandler ran out of scripted responses.");
        return _script.Dequeue()();
    }
}

internal record CapturedRequest(HttpMethod Method, Uri Uri, string? ApiKey, string? ContentType, string Body);

/// <summary>Terse builders for the canned HTTP responses the fake handler hands back.</summary>
internal static class HttpResponses
{
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Audio(byte[] bytes, string mediaType = "audio/mpeg", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(mediaType) } } };

    public static HttpResponseMessage Error(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body) };
}
