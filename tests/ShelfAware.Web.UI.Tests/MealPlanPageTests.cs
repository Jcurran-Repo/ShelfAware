using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
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

    /// <summary>A fake job runner: records who was Started and returns a scripted <see cref="Current"/>
    /// snapshot (the page polls it). No detached task — that's the real runner's own concern.</summary>
    private sealed class FakeMealPlanJobs : IMealPlanJobs
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
    private sealed class NoopGenerator : IMealPlanGenerator
    {
        public Task<IReadOnlyList<RecipeSuggestion>> GenerateAsync(MealPlanBatch batch, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecipeSuggestion>>([]);
    }
}
