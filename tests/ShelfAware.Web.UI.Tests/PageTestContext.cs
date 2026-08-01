using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Speech;
using ShelfAware.Web.Components;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

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
/// </summary>
public abstract class PageTestContext : BunitContext
{
    internal TestDb Db { get; }
    internal FlakyDbFactory Factory { get; }
    internal EfPantryStore Store { get; }
    internal FakeAppSettings AppSettings { get; }
    internal FakePantryChat Chat { get; }
    internal FakeRecipeAdapter Adapter { get; }
    internal FakeSuggestionAdvisor SuggestionAdvisor { get; }
    internal FakeAlternativesAdvisor AlternativesAdvisor { get; }
    internal FakeSubstituteAdvisor SubstituteAdvisor { get; }
    internal FakeVoiceCredentials Voice { get; }
    internal VoiceCoordinator Coordinator { get; }

    protected PageTestContext()
    {
        Db = new TestDb();
        Factory = new FlakyDbFactory(Db);
        Store = new EfPantryStore(Factory);
        AppSettings = new FakeAppSettings();
        Chat = new FakePantryChat();
        Adapter = new FakeRecipeAdapter();
        SuggestionAdvisor = new FakeSuggestionAdvisor();
        AlternativesAdvisor = new FakeAlternativesAdvisor();
        SubstituteAdvisor = new FakeSubstituteAdvisor();
        Voice = new FakeVoiceCredentials();
        Coordinator = new VoiceCoordinator();

        Services.AddSingleton<IHouseholdDbFactory>(Factory);
        Services.AddSingleton<IAppSettings>(AppSettings);
        Services.AddSingleton<IPantryStore>(Store);
        Services.AddSingleton<IPantryChat>(Chat);
        Services.AddSingleton<IRecipeAdvisor>(SuggestionAdvisor);
        Services.AddSingleton<IRecipeAdapter>(Adapter);
        Services.AddSingleton<IIngredientAlternativesAdvisor>(AlternativesAdvisor);
        Services.AddSingleton<IProductSubstituteAdvisor>(SubstituteAdvisor);
        Services.AddSingleton<IVoiceCredentials>(Voice);
        Services.AddSingleton(Coordinator);
        Services.AddSingleton(new ProductRenameService(Factory));
        Services.AddSingleton(new ProductMergeService(Factory));
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Db.Dispose();
    }
}
