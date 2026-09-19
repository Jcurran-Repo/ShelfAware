using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;

namespace ShelfAware.Tests;

/// <summary>The meal-plan setup defaults and the per-meal override resolution (a meal's own calorie/effort,
/// or the plan default when it sets none).</summary>
public class MealPlanSettingsTests
{
    [Fact]
    public void The_default_line_up_is_one_dinner_at_standard_effort()
    {
        var settings = new MealPlanSettings();
        Assert.Equal([MealSlot.Dinner], settings.Meals.Select(m => m.Slot));
        Assert.Equal(TimeEffort.Standard, settings.DefaultEffort);
        Assert.Null(settings.DefaultCalories);
    }

    [Fact]
    public void Calories_for_a_meal_is_its_override_when_set()
    {
        var settings = new MealPlanSettings { DefaultCalories = 500 };
        Assert.Equal(150, settings.CaloriesFor(new MealEntry { Calories = 150 }));
    }

    [Fact]
    public void Calories_for_a_meal_falls_back_to_the_default_when_unset()
    {
        var settings = new MealPlanSettings { DefaultCalories = 500 };
        Assert.Equal(500, settings.CaloriesFor(new MealEntry())); // no override → the plan default
    }

    [Fact]
    public void Effort_for_a_meal_is_its_override_when_set()
    {
        var settings = new MealPlanSettings { DefaultEffort = TimeEffort.Standard };
        Assert.Equal(TimeEffort.Quick, settings.EffortFor(new MealEntry { Effort = TimeEffort.Quick }));
    }

    [Fact]
    public void Effort_for_a_meal_falls_back_to_the_default_when_unset()
    {
        var settings = new MealPlanSettings { DefaultEffort = TimeEffort.Ambitious };
        Assert.Equal(TimeEffort.Ambitious, settings.EffortFor(new MealEntry())); // no override → the plan default
    }

    // ------------------------------------------------------------------ how big is this plan?

    [Theory]
    [InlineData(7, 1, 7)]        // a week of dinners
    [InlineData(7, 3, 21)]       // a week of three meals a day
    [InlineData(1, 1, 1)]        // the smallest plan there is
    public void A_plans_size_is_its_days_times_its_meals_a_day(int days, int mealsPerDay, int expected) =>
        // ⚠️ ONE definition, and this is why it is worth its own test: the meal-plan page QUOTES a price
        // from this count before Generate is pressed, and MealPlanService opens the charging scope with it
        // after. Two answers here would be a quoted price the charge then contradicted.
        Assert.Equal(expected, MealPlanSettings.SlotCountFor(days, mealsPerDay));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_plan_covering_no_days_still_covers_one(int days) =>
        Assert.Equal(1, MealPlanSettings.SlotCountFor(days, 1));

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void A_plan_with_no_meals_a_day_still_plans_one(int mealsPerDay) =>
        // SlotsFor substitutes a bare dinner for an empty meal list, so the count has to agree with it.
        Assert.Equal(7, MealPlanSettings.SlotCountFor(7, mealsPerDay));

    [Fact]
    public void A_plan_cannot_run_past_a_month_however_many_days_are_asked_for() =>
        Assert.Equal(MealPlanSettings.MaxDays, MealPlanSettings.SlotCountFor(365, 1));

    [Fact]
    public void A_plan_is_capped_at_the_slots_the_generator_will_actually_fill()
    {
        // 31 days x 4 meals is 124, the cap exactly; asking for more is capped rather than silently spent.
        // ⚠️ The cap is a PRICE now as well as a spend guard — 124 meals is 42 credits, and a household
        // that asked for 5 meals a day must not be quoted or charged for 155.
        Assert.Equal(MealPlanSettings.MaxSlots, MealPlanSettings.SlotCountFor(31, 4));
        Assert.Equal(MealPlanSettings.MaxSlots, MealPlanSettings.SlotCountFor(31, 5));
    }

    [Fact]
    public void A_settings_object_counts_its_own_slots_the_same_way() =>
        // The instance property and the static one are the same answer, so a caller holding either gets it.
        Assert.Equal(
            MealPlanSettings.SlotCountFor(14, 2),
            new MealPlanSettings { Days = 14, Meals = [new MealEntry(), new MealEntry()] }.SlotCount);
}
