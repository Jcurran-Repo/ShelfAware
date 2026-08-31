using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

/// <summary>
/// The pantry projection (§6): what the plan needs from the store, derived from planned meals + what's on
/// hand. A missing MAIN ingredient becomes a plan shop item with a buy-before due date; an on-hand one (by
/// name, grounded product, or substitute) does not; items dedupe by FOOD and bucket into Buy now / Coming up.
/// </summary>
public class MealPlanProjectionTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static PlannedMealView Meal(int dayOffset, string recipe, params string[] mainIngredients) =>
        new(Today.AddDays(dayOffset), MealSlot.Dinner, recipe,
            [.. mainIngredients.Select(n => new PlannedIngredient(n, null))]);

    private static PantryProduct OnHand(string name, params string[] alsoWorksAs) => new(name, alsoWorksAs);

    [Fact]
    public void A_missing_main_ingredient_becomes_a_shop_item_a_held_one_does_not()
    {
        var meals = new[] { Meal(3, "Chicken & Rice", "chicken breast", "white rice") };
        var onHand = new[] { OnHand("White Rice") };   // have rice, not chicken

        var items = MealPlanProjection.ShopItems(meals, onHand, Today);

        var item = Assert.Single(items);
        Assert.Equal("chicken breast", item.Name);
        Assert.Equal("Chicken & Rice", item.RecipeName);
    }

    [Fact]
    public void The_due_date_is_the_meal_date_minus_the_lead_floored_at_today()
    {
        // Meal in 5 days, lead 2 → due in 3.
        var items = MealPlanProjection.ShopItems([Meal(5, "Tacos", "ground beef")], [], Today);
        Assert.Equal(Today.AddDays(3), Assert.Single(items).DueDate);
        Assert.Equal(3, items[0].DaysUntil);

        // Meal tomorrow, lead 2 → due date can't be in the past, floors at today.
        var soon = MealPlanProjection.ShopItems([Meal(1, "Tacos", "ground beef")], [], Today);
        Assert.Equal(Today, Assert.Single(soon).DueDate);
        Assert.Equal(0, soon[0].DaysUntil);
    }

    [Fact]
    public void Items_bucket_into_buy_now_or_coming_up_by_the_seven_day_window()
    {
        // Due in 3 (meal +5) → buy now; due in 10 (meal +12) → coming up.
        var items = MealPlanProjection.ShopItems(
            [Meal(5, "Soon", "ground beef"), Meal(12, "Later", "salmon fillet")], [], Today);

        Assert.False(items.Single(i => i.Name == "ground beef").ComingUp);
        Assert.True(items.Single(i => i.Name == "salmon fillet").ComingUp);
    }

    [Fact]
    public void Meals_beyond_the_horizon_or_in_the_past_produce_no_items()
    {
        var meals = new[]
        {
            Meal(-1, "Yesterday", "steak"),   // past
            Meal(20, "Far off", "lamb chop"), // beyond the 14-day horizon
        };
        Assert.Empty(MealPlanProjection.ShopItems(meals, [], Today));
    }

    [Fact]
    public void The_same_food_across_two_meals_is_one_item_dated_to_the_earliest_meal()
    {
        // "boneless chicken breast" (day 6) and "chicken breast" (day 3) are the same food → one item, and
        // the earlier meal sets the due date (day 3 − lead 2 = day 1).
        var meals = new[]
        {
            Meal(6, "Later dish", "boneless chicken breast"),
            Meal(3, "Earlier dish", "chicken breast"),
        };

        var item = Assert.Single(MealPlanProjection.ShopItems(meals, [], Today));
        Assert.Equal(Today.AddDays(1), item.DueDate);       // earliest meal drives the date
        Assert.Equal("Earlier dish", item.RecipeName);
    }

    [Fact]
    public void A_substitute_on_hand_covers_the_ingredient()
    {
        // The recipe needs "chicken breast"; you have tenderloins that "also work as" it → not a shop item.
        var meals = new[] { Meal(3, "Chicken dish", "chicken breast") };
        var onHand = new[] { OnHand("Chicken Breast Tenderloins", "chicken breast") };

        Assert.Empty(MealPlanProjection.ShopItems(meals, onHand, Today));
    }

    [Fact]
    public void A_grounded_matched_product_on_hand_covers_the_ingredient()
    {
        // The saved recipe grounded "the good beef" to a product you have → covered even though the names
        // don't share words.
        var meals = new[]
        {
            new PlannedMealView(Today.AddDays(3), MealSlot.Dinner, "Beef dish",
                [new PlannedIngredient("the good beef", MatchedProduct: "Ground Chuck")]),
        };
        var onHand = new[] { OnHand("Ground Chuck") };

        Assert.Empty(MealPlanProjection.ShopItems(meals, onHand, Today));
    }

    [Fact]
    public void Items_are_sorted_by_due_date()
    {
        var meals = new[]
        {
            Meal(12, "Later", "salmon fillet"),
            Meal(3, "Sooner", "ground beef"),
            Meal(7, "Middle", "pork chop"),
        };

        var names = MealPlanProjection.ShopItems(meals, [], Today).Select(i => i.Name).ToList();
        Assert.Equal(["ground beef", "pork chop", "salmon fillet"], names);
    }

    [Fact]
    public void A_meal_today_still_produces_a_shop_item()
    {
        // The horizon starts AT today (>=), not after it.
        var item = Assert.Single(MealPlanProjection.ShopItems([Meal(0, "Tonight", "ground beef")], [], Today));
        Assert.Equal("ground beef", item.Name);
        Assert.Equal(Today, item.DueDate); // meal today, lead floored → due today
    }

    [Fact]
    public void A_meal_on_the_last_day_of_the_horizon_is_included()
    {
        // Day == today + HorizonDays is the last INCLUDED day (<=), day after is out.
        var onEdge = MealPlanProjection.ShopItems([Meal(MealPlanProjection.HorizonDays, "Edge", "salmon fillet")], [], Today);
        Assert.Single(onEdge);
        var justPast = MealPlanProjection.ShopItems([Meal(MealPlanProjection.HorizonDays + 1, "Past", "salmon fillet")], [], Today);
        Assert.Empty(justPast);
    }

    [Fact]
    public void A_due_date_exactly_at_the_coming_up_window_is_still_buy_now()
    {
        // due == ComingUpDays → Buy now (the split is strictly greater-than). Meal at ComingUpDays + lead.
        var item = Assert.Single(MealPlanProjection.ShopItems(
            [Meal(MealPlanProjection.ComingUpDays + MealPlanProjection.LeadDays, "Boundary", "pork chop")], [], Today));
        Assert.Equal(MealPlanProjection.ComingUpDays, item.DaysUntil);
        Assert.False(item.ComingUp);
    }

    [Fact]
    public void Two_items_due_the_same_day_sort_by_name()
    {
        // One meal, two missing mains → two items with the same due date; the tie-break is the name.
        var items = MealPlanProjection.ShopItems([Meal(3, "Big Dinner", "zucchini", "apples")], [], Today);
        Assert.Equal(["apples", "zucchini"], items.Select(i => i.Name));
    }

    [Fact]
    public void A_blank_ingredient_name_is_ignored()
    {
        var meals = new[]
        {
            new PlannedMealView(Today.AddDays(3), MealSlot.Dinner, "Odd",
                [new PlannedIngredient("  ", null), new PlannedIngredient("ground beef", null)]),
        };

        Assert.Equal("ground beef", Assert.Single(MealPlanProjection.ShopItems(meals, [], Today)).Name);
    }
}
