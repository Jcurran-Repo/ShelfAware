using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// How the /recipes page treats meal-plan recipes: it does NOT list plan-generated ones (they live on the
/// Cookbook's "Meal-plan recipes" tab), and it refuses to delete a recipe the current plan references —
/// deleting it would cascade-delete that calendar day (a recipe is a required FK parent of PlannedMeal).
/// </summary>
public class RecipesPlanInteractionTests : PageTestContext
{
    private int SeedRecipe(string name, bool planGenerated = false)
    {
        using var db = Db.CreateDbContext();
        var recipe = new Recipe
        {
            Name = name,
            SavedAt = DateTimeOffset.Now,
            PlanGenerated = planGenerated,
            Ingredients = [new RecipeIngredient { Name = "beef", IsMain = true }],
        };
        db.Recipes.Add(recipe);
        db.SaveChanges();
        return recipe.Id;
    }

    // Put an existing recipe into a current meal plan (one planned meal today).
    private void PlanUses(int recipeId)
    {
        using var db = Db.CreateDbContext();
        var plan = new MealPlan { CreatedAt = DateTimeOffset.Now, StartDate = Today, Days = 1 };
        plan.Meals.Add(new PlannedMeal { RecipeId = recipeId, Date = Today, Slot = MealSlot.Dinner });
        db.MealPlans.Add(plan);
        db.SaveChanges();
    }

    private IRenderedComponent<Recipes> RenderRecipes()
    {
        var cut = Render<Recipes>();
        cut.WaitForState(() => cut.FindAll("section.panel").Count > 0);
        return cut;
    }

    [Fact]
    public void Plan_generated_recipes_are_not_listed_on_the_recipes_page()
    {
        SeedRecipe("My Own Recipe");
        SeedRecipe("Plan Generated Dish", planGenerated: true);

        var cut = RenderRecipes();

        Assert.Contains("My Own Recipe", cut.Markup);
        Assert.DoesNotContain("Plan Generated Dish", cut.Markup); // belongs to the Cookbook's meal-plan tab
    }

    [Fact]
    public void Deleting_a_recipe_the_plan_uses_is_refused_and_keeps_it()
    {
        var id = SeedRecipe("Shared Dish");   // a user recipe the plan reused (not plan-generated)
        PlanUses(id);

        var cut = RenderRecipes();
        cut.Find("button[aria-label='Delete Shared Dish']").Click();

        Assert.NotEmpty(cut.FindAll("p.error"));                 // refused with a reason…
        using var db = Db.CreateDbContext();
        Assert.NotNull(db.Recipes.Find(id));                    // …and the recipe (and its planned meal) survive
        Assert.Equal(1, db.PlannedMeals.Count());
    }

    [Fact]
    public void Deleting_a_recipe_no_plan_uses_still_works()
    {
        SeedRecipe("Disposable");

        var cut = RenderRecipes();
        cut.Find("button[aria-label='Delete Disposable']").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Disposable", cut.Markup));
        using var db = Db.CreateDbContext();
        Assert.Empty(db.Recipes.ToList());
    }
}
