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
}
