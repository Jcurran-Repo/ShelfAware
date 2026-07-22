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

