using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// Phase 3b — the meal plan on the grocery list. A planned meal's missing MAIN ingredient becomes a
/// tinted, tagged "Plan" row with a "for &lt;recipe&gt;" note; an on-hand ingredient does not; and a food
/// the predictor ALSO lists shows once (the richer predictor row, annotated "also for …").
/// </summary>
public class GroceryListPlanTests : PageTestContext
{
    // An overdue product — lands in "Buy now" as a predictor row (and is NOT on hand, so the plan wants it too).
    private int SeedOverdue(string name, Category category = Category.Meat)
    {
        using var db = Db.CreateDbContext();
        var p = new Product
        {
            Name = name,
            Category = category,
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
            ],
        };
        db.Products.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    // A freshly-bought product — on hand (not overdue), so an ingredient it covers is NOT a plan shop item.
    private void SeedOnHand(string name, Category category = Category.Pantry)
    {
        using var db = Db.CreateDbContext();
        db.Products.Add(new Product
        {
            Name = name,
            Category = category,
            Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-2), Quantity = 1m }],
        });
        db.SaveChanges();
    }

    // A current plan with one dinner, dated within the horizon, needing the given MAIN ingredients.
    private void SeedPlanMeal(int dayOffset, string recipe, params string[] mainIngredients)
    {
        using var db = Db.CreateDbContext();
        var plan = new MealPlan { CreatedAt = DateTimeOffset.Now, StartDate = Today, Days = 7 };
        plan.Meals.Add(new PlannedMeal
        {
            Date = Today.AddDays(dayOffset),
            Slot = MealSlot.Dinner,
            Recipe = new Recipe
            {
                Name = recipe,
                SavedAt = DateTimeOffset.Now,
                PlanGenerated = true,
                Steps = [new RecipeStep { Order = 1, Text = "Cook." }],
                Ingredients = [.. mainIngredients.Select(n => new RecipeIngredient { Name = n, IsMain = true })],
            },
        });
        db.MealPlans.Add(plan);
        db.SaveChanges();
    }

    private IRenderedComponent<GroceryList> RenderList()
    {
        var cut = Render<GroceryList>();
        cut.WaitForState(() => cut.FindAll(".extras").Count > 0);
        return cut;
    }

    [Fact]
    public void A_missing_plan_ingredient_becomes_a_tagged_plan_row()
    {
        SeedPlanMeal(3, "Fresh Salsa", "cilantro"); // nothing on hand covers cilantro

        var cut = RenderList();

        var planRow = cut.FindAll("tr.grocery-src-plan").Single();
        Assert.Contains("cilantro", planRow.TextContent);
        Assert.Contains("Plan", planRow.QuerySelector(".chip-plan")!.TextContent); // tagged, not color-only
        Assert.Contains("Fresh Salsa", planRow.TextContent);                       // for <recipe>
    }

    [Fact]
    public void An_on_hand_ingredient_is_not_a_plan_row()
    {
        SeedOnHand("White Rice");
        SeedPlanMeal(3, "Rice Bowl", "white rice");

        var cut = RenderList();

        Assert.Empty(cut.FindAll("tr.grocery-src-plan"));
    }

    [Fact]
    public void A_food_the_predictor_and_plan_both_want_shows_once_annotated()
    {
        SeedOverdue("Ground Beef");             // predictor lists it (Buy now) and it's not on hand
        SeedPlanMeal(3, "Beef Tacos", "ground beef");

        var cut = RenderList();

        // No separate plan row — the predictor row absorbed it…
        Assert.Empty(cut.FindAll("tr.grocery-src-plan"));
        // …and wears the "also for Beef Tacos" annotation.
        Assert.Contains("Beef Tacos", cut.Find(".plan-for").TextContent);
        Assert.Contains("also for", cut.Find(".plan-for").TextContent);
    }
}
