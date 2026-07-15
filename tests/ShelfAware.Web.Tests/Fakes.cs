using ShelfAware.Core.Extraction;
using ShelfAware.Core.Ingest;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>A fixed household, standing in for the scope resolution (claim / circuit auth state) that only
/// exists in a real request. Null models an unauthenticated scope.</summary>
internal sealed class FakeCurrentHousehold(string? id = "household-under-test") : ICurrentHousehold
{
    private string? _id = id;

    public ValueTask<string?> GetIdAsync(CancellationToken cancellationToken = default) => new(_id);

    public ValueTask<string> GetRequiredIdAsync(CancellationToken cancellationToken = default) =>
        _id is null
            ? throw new InvalidOperationException("No household in scope.") // mirrors the real one: never fall through to nobody's pantry
            : new(_id);

    public void UseFixed(string householdId) => _id = householdId;
}

/// <summary>Returns a canned adaptation and records what it was asked with — drives RecipeAdapter tests.</summary>
internal sealed class FakeRecipeAdvisor(RecipeSuggestion? adaptResult) : IRecipeAdvisor
{
    public string? LastPreference { get; private set; }
    public IReadOnlyList<PantryProduct>? LastOnHand { get; private set; }
    public RecipeToAdapt? LastRecipe { get; private set; }

    public Task<IReadOnlyList<RecipeSuggestion>> SuggestAsync(
        string request, IReadOnlyList<string> onHand, IReadOnlyList<string> excludedFoods, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecipeSuggestion>>([]);

    public Task<RecipeSuggestion?> AdaptAsync(
        RecipeToAdapt recipe, IReadOnlyList<PantryProduct> onHand, IReadOnlyList<string> excludedFoods,
        string? preference = null, CancellationToken cancellationToken = default)
    {
        LastPreference = preference;
        LastOnHand = onHand;
        LastRecipe = recipe;
        return Task.FromResult(adaptResult);
    }
}

/// <summary>In-memory <see cref="IAppSettings"/> — a dictionary.</summary>
internal sealed class FakeAppSettings : IAppSettings
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="IReceiptInbox"/> — file name → bytes.</summary>
internal sealed class FakeInbox : IReceiptInbox
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Configured { get; set; } = true;

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Configured);

    public Task<IReadOnlyList<InboxItem>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InboxItem>>(
            Files.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new InboxItem(n, n, "image/jpeg")).ToList());

    public Task<byte[]> ReadAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files[id]);
}

/// <summary>
/// Scripted <see cref="IReceiptExtractor"/>: hands back queued results in order (repeating the last
/// one) and records what it was called with — including the tag vocabulary, so tests can assert the
/// importer feeds it. Optional <see cref="Delay"/> lets the concurrency test hold a scan open.
/// </summary>
internal sealed class FakeExtractor(params ExtractionResult[] script) : IReceiptExtractor
{
    private readonly Queue<ExtractionResult> _script = new(script);
    private ExtractionResult? _last;

    public int Calls { get; private set; }
    public IReadOnlyList<string>? LastKnownProductNames { get; private set; }
    public IReadOnlyList<string>? LastKnownTags { get; private set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public async Task<ExtractionResult> ExtractAsync(
        IReadOnlyList<ReceiptAttachment> attachments,
        IReadOnlyList<string>? knownProductNames = null,
        IReadOnlyList<string>? knownTags = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastKnownProductNames = knownProductNames;
        LastKnownTags = knownTags;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (_script.Count > 0) _last = _script.Dequeue();
        return _last ?? throw new InvalidOperationException("FakeExtractor has no scripted result.");
    }
}
