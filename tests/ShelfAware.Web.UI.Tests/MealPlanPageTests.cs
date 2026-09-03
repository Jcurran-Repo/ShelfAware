using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The /meal-plan page. Generation runs as a DETACHED background job (so it survives navigating away), so
/// the page is tested against a fake <see cref="IMealPlanJobs"/> whose status it polls — the empty prompt,
/// starting a job (and showing "Planning…"), resuming an in-flight job on return, picking up the finished
/// plan, and the failure path. The real background runner is integration-level (live-verified); the plan
/// assembly is covered in the persistence suite.
/// </summary>
public class MealPlanPageTests : PageTestContext
{
    private readonly FakeMealPlanJobs _jobs = new();

    protected override void RegisterAdditionalServices()
    {
        Services.AddScoped<ICurrentHousehold>(_ => new FakeCurrentHousehold("hh-test"));
        Services.AddSingleton<IMealPlanJobs>(_jobs);
        // The page injects MealPlanService for load/save/read (the fake Jobs never calls its generator).
        Services.AddScoped(sp => new MealPlanService(
            Factory, new NoopGenerator(), sp.GetRequiredService<IAppSettings>(), NullLogger<MealPlanService>.Instance));
    }

    private IRenderedComponent<MealPlanPage> RenderPage()
    {
        var cut = Render<MealPlanPage>();
        cut.WaitForState(() => cut.FindAll("section.panel").Count > 0);
        return cut;
    }

    private static void Generate(IRenderedComponent<MealPlanPage> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("Generate") || b.TextContent.Contains("Regenerate")).Click();

    // Insert a finished plan straight into the DB, as the background job would have written it.
    private void SeedPlan(params string[] mealNames)
    {
        using var db = Db.CreateDbContext();
        var plan = new MealPlan { CreatedAt = DateTimeOffset.Now, StartDate = DateOnly.FromDateTime(DateTime.Today), Days = mealNames.Length };
        for (var i = 0; i < mealNames.Length; i++)
        {
            var recipe = new Recipe { Name = mealNames[i], SavedAt = DateTimeOffset.Now, PlanGenerated = true, Steps = [new RecipeStep { Order = 1, Text = "Cook." }] };
            plan.Meals.Add(new PlannedMeal { Recipe = recipe, Date = DateOnly.FromDateTime(DateTime.Today).AddDays(i), Slot = MealSlot.Dinner });
        }
        db.MealPlans.Add(plan);
        db.SaveChanges();
    }

    // A one-meal plan for today whose recipe carries a base serving count and a single main ingredient with
    // an amount — the fixture the serving box scales.
    private void SeedServingPlan(int? servings, string ingredient, string quantity)
    {
        using var db = Db.CreateDbContext();
        var recipe = new Recipe
        {
            Name = "Dinner", SavedAt = DateTimeOffset.Now, PlanGenerated = true, Servings = servings,
            Steps = [new RecipeStep { Order = 1, Text = "Cook." }],
            Ingredients = [new RecipeIngredient { Name = ingredient, IsMain = true, Quantity = quantity }],
        };
        var plan = new MealPlan { CreatedAt = DateTimeOffset.Now, StartDate = DateOnly.FromDateTime(DateTime.Today), Days = 1 };
        plan.Meals.Add(new PlannedMeal { Recipe = recipe, Date = DateOnly.FromDateTime(DateTime.Today), Slot = MealSlot.Dinner });
        db.MealPlans.Add(plan);
        db.SaveChanges();
    }

    private static MealPlanJobSnapshot Running() => new(MealPlanJobState.Running, 1, 3, 0, null);
    private static MealPlanJobSnapshot Done(int meals) => new(MealPlanJobState.Done, 3, 3, meals, null);
    private static MealPlanJobSnapshot Failed(string error) => new(MealPlanJobState.Failed, 0, 3, 0, error);

    [Fact]
    public void An_empty_household_shows_the_no_plan_prompt()
    {
        var cut = RenderPage();

        Assert.Contains("No plan yet", cut.Markup);
        Assert.Empty(cut.FindAll(".mealplan-meal"));
    }

    [Fact]
    public void Generate_starts_a_background_job_and_shows_it_planning()
    {
        // No job yet — the page loads in the normal "Generate" state; clicking Start makes one running.
        var cut = RenderPage();

        Generate(cut);

        cut.WaitForAssertion(() => Assert.Contains("Planning", cut.Markup));
        Assert.Contains("hh-test", _jobs.Started);                 // a detached job was started for the household
        // …and the reassurance that leaving is safe is on screen.
        Assert.Contains("leave this page", cut.Markup);
    }

    [Fact]
    public void A_running_job_is_resumed_when_the_user_returns_to_the_page()
    {
        // The user left mid-generation and came back — the page finds the running job and shows it.
        _jobs.Snapshot = Running();

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("Planning", cut.Markup));
    }

    [Fact]
    public void The_page_picks_up_the_finished_plan_from_the_job()
    {
        SeedPlan("Tacos", "Stir Fry");   // the job wrote the plan to the DB…
        _jobs.Snapshot = Done(2);        // …and reports it done
        var cut = RenderPage();

        Generate(cut);

        cut.WaitForAssertion(() =>
        {
            // Both meals appear as titles in the calendar grid…
            Assert.Equal(2, cut.FindAll(".mealcal-meal").Count);
            Assert.Contains("Tacos", cut.Markup);
            Assert.Contains("Stir Fry", cut.Markup);
            // …and today's meal is expanded in the detail panel below.
            Assert.Single(cut.FindAll(".mealcal-detail .mealplan-meal"));
            Assert.Contains("Tacos", cut.Find(".mealcal-detail").TextContent);
            Assert.Contains("Planned 2 meals.", cut.Markup);
        });
    }

    [Fact]
    public void Clicking_a_day_expands_that_days_meals()
    {
        SeedPlan("Tacos", "Stir Fry");   // Tacos today, Stir Fry tomorrow (SeedPlan dates them day-by-day)
        var cut = RenderPage();           // the plan loads on init; the detail defaults to today

        Assert.Contains("Tacos", cut.Find(".mealcal-detail").TextContent);

        // Click the day cell showing "Stir Fry" — its detail replaces today's.
        cut.FindAll(".mealcal-day").First(d => d.TextContent.Contains("Stir Fry")).Click();

        var detail = cut.Find(".mealcal-detail").TextContent;
        Assert.Contains("Stir Fry", detail);
        Assert.DoesNotContain("Tacos", detail);
    }

    [Fact]
    public void The_reroll_button_calls_the_service()
    {
        // The base fake generator returns nothing, so a reroll comes back a soft failure — enough to prove
        // the button on a meal card is wired to RerollAsync (the swap logic itself is service-tested).
        SeedPlan("Tacos");
        var cut = RenderPage();

        Assert.NotEmpty(cut.FindAll(".mealcal-reroll"));
        cut.Find(".mealcal-reroll").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("p.error")));
    }

    [Fact]
    public void A_failed_job_shows_an_error()
    {
        _jobs.Snapshot = Failed("Couldn't generate a plan just now — please try again.");
        var cut = RenderPage();

        Generate(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("p.error")));
        Assert.Empty(cut.FindAll(".mealplan-meal"));
    }

    [Fact]
    public void Add_a_meal_appends_a_row()
    {
        var cut = RenderPage();

        Assert.Single(cut.FindAll(".mealplan-mealrow"));   // one dinner row by default
        cut.Find(".mealplan-addmeal").Click();

        Assert.Equal(2, cut.FindAll(".mealplan-mealrow").Count);
    }

    [Fact]
    public async Task Generating_saves_the_per_meal_line_up_with_its_overrides()
    {
        // The write path that matters: a per-meal override typed into a row must reach the saved settings
        // (which the generator then expands into per-slot targets). Second row → a 150-cal quick snack.
        var cut = RenderPage();
        cut.Find(".mealplan-addmeal").Click();

        // Each Change re-renders (via @bind/@onchange), so re-query the row fresh before each edit — the
        // captured element handle goes stale after the previous render (a documented bUnit gotcha).
        cut.FindAll(".mealplan-mealrow")[1].QuerySelector("select[aria-label='Meal type']")!.Change("Snack");
        cut.FindAll(".mealplan-mealrow")[1].QuerySelector("input[type='number']")!.Change("150");
        cut.FindAll(".mealplan-mealrow")[1].QuerySelector("select[aria-label*='Effort']")!.Change("Quick");

        Generate(cut);

        var saved = await Services.GetRequiredService<MealPlanService>().LoadSettingsAsync();
        Assert.Equal(2, saved.Meals.Count);
        var snack = Assert.Single(saved.Meals, m => m.Slot == MealSlot.Snack);
        Assert.Equal(150, snack.Calories);                 // per-meal override survived to the store
        Assert.Equal(TimeEffort.Quick, snack.Effort);
    }

    [Fact]
    public void The_serving_box_defaults_to_the_recipes_base_and_scales_the_amounts_live()
    {
        SeedServingPlan(servings: 2, ingredient: "White Rice", quantity: "2 cups");
        var cut = RenderPage();

        var detail = cut.Find(".mealcal-detail");
        Assert.Contains("Servings", detail.TextContent);                        // a real servings box (base known)
        Assert.Equal("2", cut.Find(".serving-count").GetAttribute("value"));    // defaults to the recipe's base
        Assert.Contains("2 cups", detail.TextContent);                          // the amount as written

        cut.Find("button[aria-label='More servings']").Click();                 // 2 → 3 servings

        Assert.Contains("3 cups", cut.Find(".mealcal-detail").TextContent);     // 2 cups × 3/2, scaled live
    }

    [Fact]
    public void The_serving_box_scales_the_amounts_down_when_you_lower_the_count()
    {
        SeedServingPlan(servings: 4, ingredient: "Ground Beef", quantity: "2 lbs");
        var cut = RenderPage();

        cut.Find(".serving-count").Change("2");   // 4 → 2 servings, factor 0.5

        Assert.Contains("1 lb", cut.Find(".mealcal-detail").TextContent);   // halved and singularised
    }

    [Fact]
    public void With_no_base_servings_the_box_is_a_plain_batch_multiplier()
    {
        SeedServingPlan(servings: null, ingredient: "White Rice", quantity: "2 cups");
        var cut = RenderPage();

        var detail = cut.Find(".mealcal-detail");
        Assert.Contains("Batch", detail.TextContent);                          // a multiplier, not "Servings"
        Assert.DoesNotContain("Servings", detail.TextContent);
        Assert.Equal("1", cut.Find(".serving-count").GetAttribute("value"));   // ×1 by default
        Assert.Contains("2 cups", detail.TextContent);                          // unchanged at ×1

        cut.Find(".serving-count").Change("3");                                 // ×3

        Assert.Contains("6 cups", cut.Find(".mealcal-detail").TextContent);
    }

    /// <summary>A fake job runner: records who was Started and returns a scripted <see cref="Current"/>
    /// snapshot (the page polls it). No detached task — that's the real runner's own concern.</summary>
    internal sealed class FakeMealPlanJobs : IMealPlanJobs
    {
        public List<string> Started { get; } = [];
        public MealPlanJobSnapshot? Snapshot { get; set; }
        // Starting a fresh job makes it "running" (unless a test pre-scripted a Done/Failed result to be
        // picked up), mirroring the real runner where Start → the job is now in flight.
        public void Start(string householdId)
        {
            Started.Add(householdId);
            Snapshot ??= new MealPlanJobSnapshot(MealPlanJobState.Running, 0, 3, 0, null);
        }
        public MealPlanJobSnapshot? Current(string householdId) => Snapshot;
    }

    /// <summary>Unused by these tests (the fake Jobs never generates), but MealPlanService needs one.</summary>
    internal sealed class NoopGenerator : IMealPlanGenerator
    {
        public Task<IReadOnlyList<RecipeSuggestion>> GenerateAsync(MealPlanBatch batch, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecipeSuggestion>>([]);
    }
}

/// <summary>Phase 4c wiring on the meal-plan page: when AI is unavailable (managed, out of credits), a
/// blocked Generate must SAY the reason and NOT start the detached job — and (the re-gate's LOW-3) must
/// still SAVE the setup form first, since the pre-check now sits after SaveSettingsAsync. Separate class
/// so the whole harness runs in the blocked (managed + zero-balance) AI state.</summary>
public class MealPlanPageBlockedTests : PageTestContext
{
    private readonly MealPlanPageTests.FakeMealPlanJobs _jobs = new();

    protected override void RegisterAdditionalServices()
    {
        Services.AddScoped<ICurrentHousehold>(_ => new FakeCurrentHousehold("hh-test"));
        Services.AddSingleton<IMealPlanJobs>(_jobs);
        Services.AddScoped(sp => new MealPlanService(
            Factory, new MealPlanPageTests.NoopGenerator(), sp.GetRequiredService<IAppSettings>(), NullLogger<MealPlanService>.Instance));
        // Managed + an Aware subscriber out of credits — AI is unavailable.
        Services.AddSingleton(new CircuitAiSettings(Options.Create(new LlmOptions { KeyMode = "managed", ApiKey = "server-key" })));
        Services.AddSingleton<IEntitlements>(new FakeEntitlements(HouseholdTier.Aware));
    }

    [Fact]
    public async Task A_blocked_generate_says_why_saves_the_setup_and_starts_no_job()
    {
        var cut = Render<MealPlanPage>();
        cut.WaitForState(() => cut.FindAll("section.panel").Count > 0);

        cut.FindAll("button").First(b => b.TextContent.Contains("Generate") || b.TextContent.Contains("Regenerate")).Click();

        cut.WaitForAssertion(() => Assert.Contains(AiErrorText.OutOfCredits, cut.Markup));
        Assert.Empty(_jobs.Started); // the doomed detached job was never started (the mutation-killer)
        // LOW-3: the pre-check sits AFTER SaveSettingsAsync (the setup form's only writer), so the household's
        // setup was persisted even though generation was blocked — the error above proves the pre-check ran,
        // which is downstream of the save. Awaited out here (WaitForAssertion's lambda is synchronous).
        Assert.False(string.IsNullOrEmpty(await AppSettings.GetAsync(SettingKeys.MealPlanSettings)));
    }
}
