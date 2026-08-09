using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.AI;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
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

    /// <summary>A FRESH read, the way the real store's AsNoTracking query is: writes made since an
    /// earlier read show up, and the objects handed out by that earlier read do NOT change under them.
    /// <para>⚠️ Modelling both halves is what makes staleness testable. Handing back the same live
    /// list meant a handler that re-reads a product before asking the engine about it looked identical
    /// to one that used its start-of-turn snapshot — so the bug (a purchase recorded earlier in the
    /// SAME turn being invisible, and the reply speaking a bare "Recorded" about a signal the engine
    /// had already discarded) and its fix were equally invisible here.</para></summary>
    public Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Product>>([.. Products.Select(Snapshot)]);

    /// <summary>Same fresh-read semantics as <see cref="GetProductsAsync"/>, one product — the real
    /// store's single-row query through the same household filter (null = no such product here).</summary>
    public Task<Product?> GetProductAsync(int productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Products.Where(p => p.Id == productId).Select(Snapshot).FirstOrDefault());

    private Product Snapshot(Product p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Category = p.Category,
        IsTracked = p.IsTracked,
        TrackQuantity = p.TrackQuantity,
        QuantityOnHand = p.QuantityOnHand,
        QuantityCountedAt = p.QuantityCountedAt,
        DefaultUnit = p.DefaultUnit,
        Tags = p.Tags,
        Substitutes = p.Substitutes,
        // The rows this store has been told about, materialized the way an Include would.
        Purchases =
        [
            .. p.Purchases,
            .. Purchases.Where(x => x.ProductId == p.Id)
                .Select(x => new PurchaseEvent { ProductId = x.ProductId, PurchasedAt = x.Date, Quantity = x.Qty }),
        ],
        Signals =
        [
            .. p.Signals,
            .. Signals.Where(x => x.ProductId == p.Id)
                .Select(x => new InventorySignal { ProductId = x.ProductId, Kind = x.Kind, SignaledAt = DateTimeOffset.Now }),
        ],
    };

    public List<string> KnownTags { get; } = [];

    public Task<int> CreateProductAsync(string name, Category category, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        Created.Add((name, category));
        var product = new Product
        {
            Id = 1000 + Products.Count,
            Name = name,
            Category = category,
            Tags = [.. tags.Select(t => new ProductTag { Value = t })],
        };
        Products.Add(product);
        return Task.FromResult(product.Id);
    }

    public Task<IReadOnlyList<string>> AddTagsAsync(int productId, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        var product = Products.FirstOrDefault(p => p.Id == productId);
        if (product is null) return Task.FromResult<IReadOnlyList<string>>([]);
        var added = new List<string>();
        foreach (var tag in tags)
        {
            if (product.Tags.Any(t => string.Equals(t.Value, tag, StringComparison.OrdinalIgnoreCase))) continue;
            product.Tags.Add(new ProductTag { Value = tag });
            added.Add(tag);
        }
        return Task.FromResult<IReadOnlyList<string>>(added);
    }

    public Task<IReadOnlyList<string>> GetKnownTagsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(KnownTags);

    public Task<bool> AddPurchaseAsync(int productId, DateOnly purchasedAt, decimal quantity, CancellationToken cancellationToken = default)
    {
        // Mirror the real store: the product must exist (nothing recorded otherwise), a purchase
        // re-tracks an untracked product and reports it, and the count moves through the REAL ledger.
        if (Products.FirstOrDefault(p => p.Id == productId) is not { } product) return Task.FromResult(false);
        Purchases.Add((productId, purchasedAt, quantity));
        var retracked = false;
        if (!product.IsTracked)
        {
            product.IsTracked = true;
            retracked = true;
        }
        StockLedger.Add(product, quantity);
        return Task.FromResult(retracked);
    }

    public Task RecordSignalAsync(int productId, SignalKind kind, CancellationToken cancellationToken = default)
    {
        // Mirror the real store's in-household existence rule: no signals onto unknown products.
        if (Products.Any(p => p.Id == productId)) Signals.Add((productId, kind));
        return Task.CompletedTask;
    }

    public Task SetTrackingAsync(int productId, bool tracked, CancellationToken cancellationToken = default)
    {
        if (Products.FirstOrDefault(p => p.Id == productId) is not { } product) return Task.CompletedTask;
        product.IsTracked = tracked;
        Tracking.Add((productId, tracked));
        return Task.CompletedTask;
    }

    public List<(int ProductId, DateOnly? ExpiresOn)> Expirations { get; } = [];

    public Task<bool> SetExpirationAsync(int productId, DateOnly? expiresOn, CancellationToken cancellationToken = default)
    {
        // Mirror the real store's contract: no purchases → nothing to carry a date → false.
        if (Products.FirstOrDefault(p => p.Id == productId) is not { Purchases.Count: > 0 })
            return Task.FromResult(false);
        Expirations.Add((productId, expiresOn));
        return Task.FromResult(true);
    }

    // Not a chat tool — the product page owns correcting a recorded quantity. Present so the fake
    // satisfies the port; nothing in the chat loop should reach it.
    public Task<bool> SetPurchaseQuantityAsync(int purchaseId, decimal quantity, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public List<(int ProductId, decimal Quantity, bool Relative, bool StopCounting)> Quantities { get; } = [];

    /// <summary>Mirrors the real store's REFUSALS, not just its happy path — a fake that accepts more
    /// than the thing it stands in for lets the chat layer's error branches go untested while looking
    /// covered. The moves run through the REAL StockLedger (as EfPantryStore's do), so the fake can't
    /// drift more permissive than the store by construction, and an asserted zero writes the same
    /// OutNow the real store writes.</summary>
    public Task<bool> SetQuantityAsync(
        int productId, decimal quantity, bool relative = false, bool stopCounting = false,
        CancellationToken cancellationToken = default)
    {
        if (Products.FirstOrDefault(p => p.Id == productId) is not { } product) return Task.FromResult(false);
        if (stopCounting)
        {
            StockLedger.StopCounting(product);
            Quantities.Add((productId, quantity, relative, stopCounting));
            return Task.FromResult(true);
        }
        // An absolute count below zero is refused rather than clamped (clamping would file an OutNow off
        // a typo); a relative move needs an ACTIVE baseline — unknown and dormant counts both refuse.
        if (!relative && quantity < 0) return Task.FromResult(false);
        if (relative && (!product.TrackQuantity || product.QuantityOnHand is null)) return Task.FromResult(false);
        var assertedOut = relative
            ? StockLedger.AdjustByHuman(product, quantity, DateTimeOffset.Now)
            : StockLedger.Attest(product, quantity, DateTimeOffset.Now);
        if (assertedOut) Signals.Add((productId, SignalKind.OutNow));
        Quantities.Add((productId, quantity, relative, stopCounting));
        return Task.FromResult(true);
    }

    public Task<bool> SetDefaultUnitAsync(int productId, string? unit, CancellationToken cancellationToken = default)
    {
        if (Products.All(p => p.Id != productId)) return Task.FromResult(false);
        Units.Add((productId, string.IsNullOrWhiteSpace(unit) ? null : unit.Trim()));
        return Task.FromResult(true);
    }

    public List<(int ProductId, string? Unit)> Units { get; } = [];

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
    // A read, like GetProductsAsync — only WRITES simulate failure in this fake.
    public Task<Product?> GetProductAsync(int productId, CancellationToken cancellationToken = default) =>
        Task.FromResult(products.FirstOrDefault(p => p.Id == productId));
    public Task<bool> AddPurchaseAsync(int productId, DateOnly purchasedAt, decimal quantity, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("simulated DB write failure");
    public Task<int> CreateProductAsync(string name, Category category, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> AddTagsAsync(int productId, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    // Prompt composition reads the vocabulary before any tool runs — must succeed even in this fake.
    public Task<IReadOnlyList<string>> GetKnownTagsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task RecordSignalAsync(int productId, SignalKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetTrackingAsync(int productId, bool tracked, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> SetExpirationAsync(int productId, DateOnly? expiresOn, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("simulated DB write failure");
    public Task<bool> SetQuantityAsync(
        int productId, decimal quantity, bool relative = false, bool stopCounting = false,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("simulated DB write failure");
    public Task<bool> SetPurchaseQuantityAsync(int purchaseId, decimal quantity, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("simulated DB write failure");
    public Task<bool> SetDefaultUnitAsync(int productId, string? unit, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("simulated DB write failure");
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
        // A real handler observes the token; this one must too, or a caller's cancellation looks like a
        // successful call here and only misbehaves in production.
        cancellationToken.ThrowIfCancellationRequested();

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
