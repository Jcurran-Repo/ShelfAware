using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The ElevenLabs realtime "Live agent" caret option is disabled for now behind Voice:LiveAgentEnabled
/// (default off): the paid per-minute agent is hidden pending a free-voice rethink, while the hand-rolled
/// cook-along reader ("🔊 Read it to me") stays. These pin BOTH halves — the flag gates the option even
/// when a per-circuit agent id is present, and re-enabling it still requires that agent id — so neither
/// the flag check nor the agent-id check can be dropped without a red test. (The signed-url endpoint gates
/// on the same flag in Program.cs; this harness renders only the page.)
/// </summary>
public abstract class RecipesLiveAgentTestBase : PageTestContext
{
    protected void SeedRecipeWithSteps(string name)
    {
        using var db = Db.CreateDbContext();
        db.Recipes.Add(new Recipe
        {
            Name = name,
            SavedAt = DateTimeOffset.Now,
            Ingredients = [new RecipeIngredient { Name = "beef", IsMain = true }],
            // The Cook-along caret (and so the Live-agent option) only renders for a recipe with steps.
            Steps = [new RecipeStep { Order = 1, Text = "Cook." }],
        });
        db.SaveChanges();
    }

    protected IRenderedComponent<Recipes> RenderRecipes()
    {
        var cut = Render<Recipes>();
        cut.WaitForState(() => cut.FindAll(".saved-recipes li").Count > 0);
        return cut;
    }

    // The Cook-along options (Read-aloud, Live agent) live in the split-button's caret menu, which renders
    // its children only once opened — so open it before asserting on those options.
    protected static void OpenCookAlongMenu(IRenderedComponent<Recipes> cut) => cut.Find(".split-caret").Click();

    protected static bool HasLiveAgentButton(IRenderedComponent<Recipes> cut) =>
        cut.FindAll("button").Any(b => b.TextContent.Contains("Live agent"));

    protected static bool HasReadAloudButton(IRenderedComponent<Recipes> cut) =>
        cut.FindAll("button").Any(b => b.TextContent.Contains("Read it to me"));
}

/// <summary>Default deployment: the flag is unset, so the option is off even with an agent id.</summary>
public class RecipesLiveAgentDisabledTests : RecipesLiveAgentTestBase
{
    [Fact]
    public void Hidden_when_the_flag_is_off_even_with_an_agent_id()
    {
        Voice.AgentId = "agent_x";           // a per-circuit agent id is present
        SeedRecipeWithSteps("Beef Stew");

        var cut = RenderRecipes();
        OpenCookAlongMenu(cut);

        // The reader option rendered (the hand-rolled reader is always available) — so the Live-agent
        // option is absent because the flag gated it, not because the menu didn't open.
        Assert.True(HasReadAloudButton(cut));
        Assert.False(HasLiveAgentButton(cut));
    }
}

/// <summary>Flag on: the option returns — but still only when a per-circuit agent id is present.</summary>
public class RecipesLiveAgentEnabledTests : RecipesLiveAgentTestBase
{
    protected override void RegisterAdditionalServices() =>
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Voice:LiveAgentEnabled"] = "true" })
            .Build());

    [Fact]
    public void Shows_when_the_flag_is_on_and_an_agent_id_is_present()
    {
        Voice.AgentId = "agent_x";
        SeedRecipeWithSteps("Beef Stew");

        var cut = RenderRecipes();
        OpenCookAlongMenu(cut);

        Assert.True(HasLiveAgentButton(cut));
    }

    [Fact]
    public void Stays_hidden_without_an_agent_id_even_with_the_flag_on()
    {
        // Voice.AgentId left empty — the flag alone must not surface the option.
        SeedRecipeWithSteps("Beef Stew");

        var cut = RenderRecipes();
        OpenCookAlongMenu(cut);

        Assert.True(HasReadAloudButton(cut));
        Assert.False(HasLiveAgentButton(cut));
    }
}
