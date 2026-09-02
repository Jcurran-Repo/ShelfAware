using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ShelfAware.Llm;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Speech;
using ShelfAware.Web.Components;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;
using ShelfAware.Web.Undo;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The page-test harness: bUnit over the SAME stack the persistence suite trusts — a real
/// in-memory-SQLite <see cref="TestDb"/> behind the household factory, the real
/// <see cref="EfPantryStore"/>, real rename/merge services — with fakes only at the AI and
/// voice seams (where ShelfAware.Llm.Tests already covers the real implementations).
///
/// Pages get their contexts through <see cref="Factory"/>, whose knobs model the boundary
/// production genuinely has: every load and save is its own short-lived context, any of which
/// can fail or stall independently of the others. The store shares the same factory, so one
/// counter can direct a failure at exactly the context under test (the store's write, or the
/// page's reload after it).
///
/// AI-adjacent child components (PushToTalk, OnboardingBanner, the voice readers) are stubbed:
/// they have their own dependencies and deserve their own tests; a page test asserts the page.
/// Pure-markup children (SplitButton, BrandVarietyHint, LineChart) render for real.
///
/// ⚠️ Never hand an async lambda to <c>WaitForAssertion</c>. It takes an <see cref="Action"/>, so the
/// lambda binds as <c>async void</c>: it returns at its first <c>await</c>, the helper observes no
/// exception, and the wait passes having pinned nothing. An assertion that must <c>await</c> —
/// anything reading the store or the DB back — goes in <c>await cut.WaitForAssertionAsync(async ()
/// =&gt; …)</c> instead; pure DOM and markup checks stay on the synchronous overload.
/// </summary>
public abstract class PageTestContext : BunitContext
{
    internal TestDb Db { get; }
    internal FlakyDbFactory Factory { get; }
    internal EfPantryStore Store { get; }
    /// <summary>The REAL activity log over the test DB, with the production undo handlers — so a page's
    /// action records and reverses for real, never through a stand-in (item 20/33's trap).</summary>
    internal ActivityLogService ActivityLog { get; }
    /// <summary>The REAL settings store over the test DB, not a dictionary. Settings are data, not one
    /// of the AI/browser seams this harness fakes — and a stand-in that holds them in memory cannot see
    /// a change the product makes to the table, which is how a page reading its own settings back after
    /// "delete my data" passed against a fake that still remembered them.</summary>
    internal IAppSettings AppSettings { get; }
    internal FakePantryChat Chat { get; }
    internal FakeRecipeAdapter Adapter { get; }
    internal FakeSuggestionAdvisor SuggestionAdvisor { get; }
    internal FakeAlternativesAdvisor AlternativesAdvisor { get; }
    internal FakeSubstituteAdvisor SubstituteAdvisor { get; }
    internal FakeVoiceCredentials Voice { get; }
    internal VoiceCoordinator Coordinator { get; }
    /// <summary>Real coordinator, not a fake — it is a plain event bus. Settings and the first-run banner
    /// both raise it, and the walkthrough itself subscribes, so a test can watch either end.</summary>
    internal TourCoordinator Tour { get; }
    /// <summary>Real courier, not a fake — a plain per-circuit slot. The footer stashes a captured snapshot
    /// into it and /bugs collects it, so a test can watch either end of that hand-off.</summary>
    internal BugReportContext BugContext { get; }

    protected PageTestContext()
    {
        Db = new TestDb();
        Factory = new FlakyDbFactory(Db);
        ActivityLog = UndoTesting.Log(Factory);
        Store = new EfPantryStore(Factory, ActivityLog);
        AppSettings = new EfAppSettings(Factory);
        Chat = new FakePantryChat();
        Adapter = new FakeRecipeAdapter();
        SuggestionAdvisor = new FakeSuggestionAdvisor();
        AlternativesAdvisor = new FakeAlternativesAdvisor();
        SubstituteAdvisor = new FakeSubstituteAdvisor();
        Voice = new FakeVoiceCredentials();
        Coordinator = new VoiceCoordinator();
        Tour = new TourCoordinator();
        BugContext = new BugReportContext();

        Services.AddSingleton<IHouseholdDbFactory>(Factory);
        Services.AddSingleton<IAppSettings>(AppSettings);
        Services.AddSingleton<IPantryStore>(Store);
        Services.AddSingleton(ActivityLog);
        Services.AddSingleton<IActivityLog>(ActivityLog);
        Services.AddSingleton<IPantryChat>(Chat);
        Services.AddSingleton<IRecipeAdvisor>(SuggestionAdvisor);
        Services.AddSingleton<IRecipeAdapter>(Adapter);
        Services.AddSingleton<IIngredientAlternativesAdvisor>(AlternativesAdvisor);
        Services.AddSingleton<IProductSubstituteAdvisor>(SubstituteAdvisor);
        Services.AddSingleton<IVoiceCredentials>(Voice);
        Services.AddSingleton(Coordinator);
        Services.AddSingleton(Tour);
        Services.AddSingleton(BugContext);
        // Keyless BYOK — the public-demo shape. A test needing the managed deployment registers its own
        // over this (RegisterAdditionalServices runs after these, so the later registration wins).
        Services.AddSingleton(new CircuitAiSettings(Options.Create(new LlmOptions())));
        Services.AddSingleton(new ProductRenameService(Factory, ActivityLog));
        Services.AddSingleton(new ProductMergeService(Factory, ActivityLog));
        // The grocery list + dashboard read the current meal plan (GetCurrentPlanAsync); they never
        // generate, so a no-op generator suffices. A test needing generation registers its own over this.
        Services.AddSingleton(new MealPlanService(Factory, new FakeMealPlanGenerator(), AppSettings, NullLogger<MealPlanService>.Instance));
        // Real (empty) IConfiguration so pages that read a deployment flag — e.g. Recipes' gate on
        // Voice:LiveAgentEnabled — resolve; an absent key is the feature's default-off state, which is what
        // a test should see. A test wanting a flag ON registers its own IConfiguration in
        // RegisterAdditionalServices (which runs after this, so it wins).
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddLogging();

        JSInterop.Mode = JSRuntimeMode.Loose;

        ComponentFactories.AddStub<PushToTalk>();
        ComponentFactories.AddStub<OnboardingBanner>();
        ComponentFactories.AddStub<RecipeReadAloud>();
        ComponentFactories.AddStub<CookAlong>();

        // Derived classes adjust services and factories here — after the standard stubs (so a
        // component-under-test can un-stub itself), and before the provider locks (the moment
        // anything resolves from it, which SetRendererInfo below triggers).
        RegisterAdditionalServices();

        // Pages run interactively in production (global InteractiveServer); Recipes reads
        // RendererInfo.IsInteractive in OnParametersSet, which throws when it's never set.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    /// <summary>Called from the base constructor after the standard registrations and BEFORE the
    /// provider initializes. ⚠️ Runs before the derived constructor body — use only the base
    /// class's own members in an override.</summary>
    protected virtual void RegisterAdditionalServices() { }

    /// <summary>Evaluated fresh on every read, the same way the pages compute their own
    /// DateTime.Today — a static captured at class load can straddle midnight and skew every
    /// relative-date fixture in a run that crosses it.</summary>
    private protected static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Visible text with whitespace collapsed — Razor's source line breaks otherwise make
    /// exact-text pins flaky. One definition for every test file.</summary>
    internal static string Collapsed(AngleSharp.Dom.IElement element) => Collapsed(element.TextContent);

    internal static string Collapsed(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Db.Dispose();
    }
}
