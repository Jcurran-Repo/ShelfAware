using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using ShelfAware.Core.Census;
using ShelfAware.Core.Chat;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Speech;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>Raises a file input's change event by hand, WITHOUT bUnit's UploadFiles, and hands back
/// the HANDLER's task. Needed wherever a test parks the ingest (staging is eager, so UploadFiles'
/// own dispatch would sit inside the parked handler with no task to await) — and it's also how a
/// re-pick of the SAME file name is driven, which the photo pages' append-across-change-events
/// behaviour turns on.</summary>
internal static class FileEvents
{
    internal sealed class FakeBrowserFile(string name, string contentType = "image/jpeg") : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.Now;
        public long Size => 3;
        public string ContentType => contentType;
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new MemoryStream([1, 2, 3]);
    }

    /// <summary>Dispatch a change on the Nth file input of the page. The photo-taking pages (receipt
    /// upload, census) render a single picker, so that's index 0.</summary>
    public static Task ChangeAsync<T>(IRenderedComponent<T> cut, int inputIndex, params string[] names)
        where T : class, IComponent =>
        cut.InvokeAsync(() => cut.FindComponents<InputFile>()[inputIndex].Instance.OnChange.InvokeAsync(
            new InputFileChangeEventArgs([.. names.Select(n => (IBrowserFile)new FakeBrowserFile(n))])));
}

/// <summary>Stands in for the browser-side downscale on BOTH photo-taking pages (the census and the
/// receipt upload stage photos through the same loader). <c>RequestImageFileAsync</c> reaches into JS
/// and throws outright under bUnit, so this is what makes the flow beneath it reachable at all.</summary>
internal sealed class StubPhotoLoader : IShelfPhotoLoader
{
    /// <summary>Thrown on EVERY load — the "all photos are bad" shape.</summary>
    public Exception? Throws { get; set; }

    /// <summary>Thrown on the NEXT load only — so a selection can mix one bad file with good
    /// neighbours and a test can watch the good ones survive it.</summary>
    public Exception? ThrowsOnce { get; set; }

    /// <summary>When set, the next load parks here — so a test can interleave something (a
    /// navigate-away, a second event) with a read that is genuinely mid-flight. One-shot, same
    /// shape as QueueReader.Hold.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Every file name this loader was asked to read, in order — how a test proves a PDF
    /// never touched the photo path, or that an ingest read exactly the files it was handed.</summary>
    public List<string> Loaded { get; } = [];

    public async Task<ShelfPhoto> LoadAsync(IBrowserFile file, CancellationToken cancellationToken = default)
    {
        if (Hold is { } gate)
        {
            Hold = null;
            await gate.Task;
        }
        // The real loader's WaitAsync(…, ct) throws the moment the token fires, gate or no gate —
        // without this the teardown tests pass vacuously (a stub that ignores cancellation can
        // never exercise the teardown-vs-alive split the pages are built around).
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowsOnce is { } once)
        {
            ThrowsOnce = null;
            throw once;
        }
        if (Throws is not null) throw Throws;
        Loaded.Add(file.Name);
        return new ShelfPhoto([1, 2, 3], "image/jpeg");
    }
}

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

/// <summary>Captures what a page LOGGED. Most page behaviour is observable in the markup, but teardown
/// behaviour is not — once a component is disposed there is nothing left to render, so "this navigate-away
/// wrote an ERROR into a real deployment's log" can only be seen here. Errors only: the level is the whole
/// point, and recording Information would bury it.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public List<(string Category, Exception? Error, string Message)> Errors { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose() { }

    private sealed class Recorder(RecordingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error) return;
            lock (owner.Errors) owner.Errors.Add((category, exception, formatter(state, exception)));
        }
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

/// <summary>The box-wide demo valve for AiErrorText's pre-check. Blocks with <paramref name="message"/>, or
/// never (null, the default) — a demo-cap page test constructs one with a message.</summary>
internal sealed class FakeDemoValve(string? message = null) : IDemoValve
{
    public ValueTask<string?> CallBlockedMessageAsync(CancellationToken cancellationToken = default) => new(message);
}

/// <summary>A no-op meal-plan generator: pages that read the current plan (the grocery list, the dashboard)
/// never generate, so MealPlanService needs a generator only to construct.</summary>
internal sealed class FakeMealPlanGenerator : IMealPlanGenerator
{
    public Task<IReadOnlyList<RecipeSuggestion>> GenerateAsync(MealPlanBatch batch, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecipeSuggestion>>([]);
}
