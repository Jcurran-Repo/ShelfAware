using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The /meal-plan page over the real MealPlanService (on the test DB) with a FAKED generator: the empty
/// prompt, generating a plan and displaying its dated meals, and the soft-failure error path. The
/// generation call itself is exercised in the Llm + persistence suites; here it's the page wiring.
/// </summary>
public class MealPlanPageTests : PageTestContext
{
    private readonly FakeMealPlanGenerator _generator = new();

    protected override void RegisterAdditionalServices()
    {
        // The page injects MealPlanService, which injects IMealPlanGenerator. Register the real service over
        // the test DB + the household-scoped IAppSettings the base harness already provides, with the fake
        // generator baked in. Base members / field-initializer state only (this runs from the base ctor).
        Services.AddScoped(sp => new MealPlanService(
            Factory, _generator, sp.GetRequiredService<IAppSettings>(), NullLogger<MealPlanService>.Instance));
    }

    private static RecipeSuggestion Meal(string name) => new(
        name, $"A {name} dinner.",
        [new SuggestedIngredient("chicken", true, null, "1 lb")],
        ["Cook it.", "Serve it."], 500);

    private IRenderedComponent<MealPlanPage> RenderPage()
    {
        var cut = Render<MealPlanPage>();
        cut.WaitForState(() => cut.FindAll("section.panel").Count > 0);
        return cut;
    }

    private static void Generate(IRenderedComponent<MealPlanPage> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("Generate") || b.TextContent.Contains("Regenerate")).Click();

    [Fact]
    public void An_empty_household_shows_the_no_plan_prompt()
    {
        var cut = RenderPage();

        Assert.Contains("No plan yet", cut.Markup);
        Assert.Empty(cut.FindAll(".mealplan-meal"));
    }

    [Fact]
    public void Generate_creates_a_plan_and_displays_its_dated_meals()
    {
        _generator.Enqueue([Meal("Tacos"), Meal("Stir Fry")]);
        var cut = RenderPage();

        Generate(cut);

        cut.WaitForAssertion(() =>
        {
            var meals = cut.FindAll(".mealplan-meal");
            Assert.Equal(2, meals.Count);
            Assert.Contains("Tacos", cut.Markup);
            Assert.Contains("Stir Fry", cut.Markup);
            Assert.Contains("Planned 2 meals.", cut.Markup);
        });
        // The button flips to Regenerate now that a plan exists.
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Regenerate");
    }

    [Fact]
    public void An_empty_model_result_shows_an_error_and_leaves_no_plan()
    {
        _generator.Enqueue([]); // the model came back with nothing
        var cut = RenderPage();

        Generate(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("p.error")));
        Assert.Empty(cut.FindAll(".mealplan-meal"));
    }

    /// <summary>A scriptable generator: returns the next queued batch (empty once exhausted).</summary>
    private sealed class FakeMealPlanGenerator : IMealPlanGenerator
    {
        private readonly Queue<IReadOnlyList<RecipeSuggestion>> _results = new();
        public void Enqueue(IReadOnlyList<RecipeSuggestion> meals) => _results.Enqueue(meals);

        public Task<IReadOnlyList<RecipeSuggestion>> GenerateAsync(MealPlanBatch batch, CancellationToken cancellationToken = default) =>
            Task.FromResult(_results.Count > 0 ? _results.Dequeue() : (IReadOnlyList<RecipeSuggestion>)[]);
    }
}
