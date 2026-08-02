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

    /// <summary>The token the most recent create was handed. A page decides per operation whether its
    /// work may be cancelled when the visitor leaves, and that decision is otherwise invisible to a
    /// test: a re-runnable READ should carry the page's token, while a one-shot WRITE over input that
    /// exists nowhere else must not. <c>CanBeCanceled</c> tells the two apart without any timing.</summary>
    public CancellationToken LastToken { get; private set; }

    public async Task<ShelfAwareDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        LastToken = cancellationToken;
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

    /// <summary>What the last call carried alongside the text — the voice surfaces' contracts are
    /// largely ABOUT these two (replayed history, and the screen/recipe context injection).</summary>
    public IReadOnlyList<ChatTurn>? LastHistory { get; private set; }
    public string? LastScreenContext { get; private set; }

    /// <summary>When set, the next call awaits this — keeps the page's busy state observable
    /// (a real model call has latency; an instant fake would make the busy branch untestable).
    /// One-shot: consumed by the call it holds.</summary>
    public TaskCompletionSource<ChatResult>? Hold { get; set; }

    public async Task<ChatResult> HandleAsync(
        string userText, IReadOnlyList<ChatTurn>? history = null, string? screenContext = null,
        CancellationToken cancellationToken = default)
    {
        Asked.Add(userText);
        LastHistory = history?.ToList();
        LastScreenContext = screenContext;
        if (Hold is { } gate)
        {
            Hold = null;
            return await gate.Task;
        }
        return Next;
    }
}

/// <summary>Transcribes from a queue — the SEQUENCER for voice-loop tests: the fake browser's
/// capture is sticky (every window "hears" the same bytes), so WHAT was said each turn is driven
/// from here. An exhausted queue answers "stop listening" so a loop under test always winds down
/// instead of spinning.</summary>
internal sealed class FakeSpeechToText : ISpeechToText
{
    public Queue<SpeechToTextResult> Results { get; } = new();
    public List<AudioClip> Heard { get; } = [];

    public Task<SpeechToTextResult> TranscribeAsync(AudioClip audio, CancellationToken cancellationToken = default)
    {
        Heard.Add(audio);
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : SpeechToTextResult.Ok("stop listening"));
    }

    public void Say(params string[] utterances)
    {
        foreach (var u in utterances) Results.Enqueue(SpeechToTextResult.Ok(u));
    }
}

/// <summary>Synthesizes one recognizable byte per call and records every (text, context) pair —
/// the narration tests' assertions are about WHAT was spoken and with WHICH neighbours, which is
/// the cache-key contract RecipeNarration exists to hold.</summary>
internal sealed class FakeTextToSpeech : ITextToSpeech
{
    public List<(string Text, SpeechContext? Context)> Spoken { get; } = [];

    /// <summary>Texts whose synthesis should fail (contains-match) — the reader's never-skip-a-step
    /// rule needs a step that won't render.</summary>
    public List<string> FailOn { get; } = [];

    public string OutputFingerprint => "fake-tts";
    public string OutputMediaType => "audio/mpeg";

    public Task<TextToSpeechResult> SynthesizeAsync(
        string text, SpeechContext? context = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Spoken.Add((text, context));
        return Task.FromResult(FailOn.Any(text.Contains)
            ? new TextToSpeechResult { Success = false, Error = "synthesis refused by test" }
            : new TextToSpeechResult { Success = true, Audio = [1], MediaType = "audio/mpeg" });
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

    /// <summary>When set, the next SuggestAsync throws it instead of answering — the page's
    /// keep-the-old-batch-on-failure rule needs a failing model call to exist.</summary>
    public Exception? Throw { get; set; }

    public IReadOnlyList<string>? LastOnHand { get; private set; }
    public IReadOnlyList<string>? LastExcluded { get; private set; }

    public Task<IReadOnlyList<RecipeSuggestion>> SuggestAsync(
        string request, IReadOnlyList<string> onHand, IReadOnlyList<string> excludedFoods,
        CancellationToken cancellationToken = default)
    {
        LastOnHand = onHand;
        LastExcluded = excludedFoods;
        return Throw is { } ex ? Task.FromException<IReadOnlyList<RecipeSuggestion>>(ex) : Task.FromResult(Suggestions);
    }

    public Task<RecipeSuggestion?> AdaptAsync(
        RecipeToAdapt recipe, IReadOnlyList<PantryProduct> onHand, IReadOnlyList<string> excludedFoods,
        string? preference = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<RecipeSuggestion?>(null);
}

internal sealed class FakeAlternativesAdvisor : IIngredientAlternativesAdvisor
{
    public IReadOnlyList<string> Alternatives { get; set; } = [];

    /// <summary>How many times the advisor was actually asked — the swap cloud promises to
    /// generate once and cache, and only a counter can prove the second open costs nothing.</summary>
    public int Calls { get; private set; }

    public Task<IReadOnlyList<string>> SuggestAsync(string ingredientName, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Alternatives);
    }
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
