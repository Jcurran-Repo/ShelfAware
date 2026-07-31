using ShelfAware.Core.Chat;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Speech;
using ShelfAware.Web.Data;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>Wraps <see cref="TestDb"/> so a test can model the boundary production pages actually
/// have: every load and every save runs on its own short-lived context, and any one of them can
/// fail independently. The knobs exist for the pages' split failure advice ("didn't save — try
/// again" vs "saved — don't repeat it"), which hinges on WHICH context in a handler's sequence
/// died — a thing no single-context test can exercise honestly.</summary>
internal sealed class FlakyDbFactory(TestDb inner) : IHouseholdDbFactory
{
    /// <summary>How many more <see cref="CreateDbContextAsync"/> calls succeed before every later
    /// call throws. 0 = fail immediately; null (the default) = never fail. Arm it right before the
    /// interaction under test — earlier loads have already spent their calls.</summary>
    public int? FailAfter { get; set; }

    /// <summary>When set, the NEXT create awaits this before returning — holding that handler
    /// mid-flight so a second UI event can interleave, the way two circuit events genuinely can.
    /// One-shot: consumed by the create it gates.</summary>
    public TaskCompletionSource? HoldNext { get; set; }

    public async Task<ShelfAwareDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (HoldNext is { } gate)
        {
            HoldNext = null;
            await gate.Task;
        }
        if (FailAfter is { } remaining)
        {
            if (remaining <= 0) throw new InvalidOperationException("Simulated database failure.");
            FailAfter = remaining - 1;
        }
        return inner.CreateDbContext();
    }
}

/// <summary>Records what the page asked and answers with whatever the test queued — the page's
/// contract with chat is "send the text, render the result", not the tool loop itself (that lives
/// in ShelfAware.Llm.Tests against the real AnthropicPantryChat).</summary>
internal sealed class FakePantryChat : IPantryChat
{
    public List<string> Asked { get; } = [];
    public ChatResult Next { get; set; } = new() { Success = true, Reply = "Done." };

    /// <summary>When set, the next call awaits this — keeps the page's busy state observable
    /// (a real model call has latency; an instant fake would make the busy branch untestable).
    /// One-shot: consumed by the call it holds.</summary>
    public TaskCompletionSource<ChatResult>? Hold { get; set; }

    public async Task<ChatResult> HandleAsync(
        string userText, IReadOnlyList<ChatTurn>? history = null, string? screenContext = null,
        CancellationToken cancellationToken = default)
    {
        Asked.Add(userText);
        if (Hold is { } gate)
        {
            Hold = null;
            return await gate.Task;
        }
        return Next;
    }
}

/// <summary>Answers every adapt with a canned result; records the ask.</summary>
internal sealed class FakeRecipeAdapter : IRecipeAdapter
{
    public AdaptResult Next { get; set; } = new(false, "No adaptation configured in this test.");
    public List<(int RecipeId, IngredientSwap? Swap)> Asked { get; } = [];

    public Task<AdaptResult> AdaptToOnHandAsync(
        int recipeId, IngredientSwap? swap = null, CancellationToken cancellationToken = default)
    {
        Asked.Add((recipeId, swap));
        return Task.FromResult(Next);
    }
}

internal sealed class FakeSuggestionAdvisor : IRecipeAdvisor
{
    public IReadOnlyList<RecipeSuggestion> Suggestions { get; set; } = [];

    public Task<IReadOnlyList<RecipeSuggestion>> SuggestAsync(
        string request, IReadOnlyList<string> onHand, IReadOnlyList<string> excludedFoods,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Suggestions);

    public Task<RecipeSuggestion?> AdaptAsync(
        RecipeToAdapt recipe, IReadOnlyList<PantryProduct> onHand, IReadOnlyList<string> excludedFoods,
        string? preference = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<RecipeSuggestion?>(null);
}

internal sealed class FakeAlternativesAdvisor : IIngredientAlternativesAdvisor
{
    public IReadOnlyList<string> Alternatives { get; set; } = [];

    public Task<IReadOnlyList<string>> SuggestAsync(string ingredientName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Alternatives);
}

internal sealed class FakeSubstituteAdvisor : IProductSubstituteAdvisor
{
    public IReadOnlyList<string> Substitutes { get; set; } = [];

    public Task<IReadOnlyList<string>> SuggestAsync(string productName, string category, CancellationToken cancellationToken = default) =>
        Task.FromResult(Substitutes);
}

internal sealed class FakeVoiceCredentials : IVoiceCredentials
{
    public string ApiKey { get; set; } = "";
    public string AgentId { get; set; } = "";
}
