using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

/// <summary>
/// The combined shopping board (§5b): predictor estimates + plan shop items merged into one set of rows,
/// deduped by food so a product wanted by both the plan and the predictor shows once (the richer predictor
/// row kept, annotated with the meal, adopting the earlier due date).
/// </summary>
public class GroceryBoardTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static ProductEstimate Estimate(string name, PredictionStatus status, int daysUntil, Category cat = Category.Meat) =>
        new()
        {
            ProductId = name.GetHashCode(),
            Name = name,
            Category = cat,
            Status = status,
            NextBuyDate = Today.AddDays(daysUntil),
            DaysUntil = daysUntil,
        };

    private static PlanShopItem Plan(string name, int dueInDays, bool comingUp = false) => new()
    {
        Name = name,
        DueDate = Today.AddDays(dueInDays),
        DaysUntil = dueInDays,
        ComingUp = comingUp,
        MealDate = Today.AddDays(dueInDays + MealPlanProjection.LeadDays),
        MealSlot = MealSlot.Dinner,
        RecipeName = "Some Dish",
    };

    [Fact]
    public void A_predictor_estimate_becomes_a_predictor_row_bucketed_by_status()
    {
        var rows = GroceryBoard.Combine(
            [Estimate("Milk", PredictionStatus.Overdue, -2), Estimate("Flour", PredictionStatus.Stocked, 20)], []);

        var milk = rows.Single(r => r.Name == "Milk");
        Assert.Equal(ShoppingSource.Predictor, milk.Source);
        Assert.True(milk.BuyNow);                                 // Overdue → Buy now
        Assert.NotNull(milk.Estimate);
        Assert.False(rows.Single(r => r.Name == "Flour").BuyNow); // Stocked → Coming up
    }

    [Fact]
    public void A_plan_item_with_no_matching_product_becomes_a_plan_row()
    {
        var rows = GroceryBoard.Combine([], [Plan("cilantro", dueInDays: 3)]);

        var row = Assert.Single(rows);
        Assert.Equal(ShoppingSource.Plan, row.Source);
        Assert.True(row.BuyNow);
        Assert.Null(row.Category);          // not a tracked product → no known aisle
        Assert.NotNull(row.PlanFor);
        Assert.Null(row.Estimate);
    }

    [Fact]
    public void A_coming_up_plan_item_is_not_buy_now()
    {
        Assert.False(Assert.Single(GroceryBoard.Combine([], [Plan("lamb", 10, comingUp: true)])).BuyNow);
    }

    [Fact]
    public void A_food_wanted_by_both_shows_one_row_the_predictor_row_annotated_with_the_meal()
    {
        // Predictor already lists "Chicken Breast" (due in 5); the plan needs "chicken breast" too.
        var rows = GroceryBoard.Combine(
            [Estimate("Chicken Breast", PredictionStatus.DueSoon, 5)],
            [Plan("chicken breast", dueInDays: 6)]);

        var row = Assert.Single(rows);                 // ONE row, not two
        Assert.Equal(ShoppingSource.Predictor, row.Source); // the richer predictor row is kept
        Assert.NotNull(row.Estimate);
        Assert.NotNull(row.PlanFor);                    // …annotated with the meal
        Assert.Equal(5, row.DaysUntil);                 // predictor was sooner, so its date stands
    }

    [Fact]
    public void When_the_plan_needs_it_sooner_the_kept_row_adopts_the_earlier_due_date_and_buy_now()
    {
        // Predictor has it Stocked (Coming up, due in 12); the plan needs it in 2 → pull it to Buy now.
        var rows = GroceryBoard.Combine(
            [Estimate("Ground Beef", PredictionStatus.Stocked, 12)],
            [Plan("ground beef", dueInDays: 2)]);

        var row = Assert.Single(rows);
        Assert.Equal(2, row.DaysUntil);   // adopted the plan's earlier due date
        Assert.True(row.BuyNow);          // …and moved into Buy now
        Assert.NotNull(row.PlanFor);
    }
}
