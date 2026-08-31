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
            Assert.Equal(2, cut.FindAll(".mealplan-meal").Count);
            Assert.Contains("Tacos", cut.Markup);
            Assert.Contains("Planned 2 meals.", cut.Markup);
        });
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
