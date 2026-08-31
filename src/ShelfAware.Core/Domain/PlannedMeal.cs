namespace ShelfAware.Core.Domain;

/// <summary>Which eating occasion a planned meal fills. A snack is just a small-calorie meal in this slot
/// — no special-casing beyond its slot and its targets.</summary>
public enum MealSlot { Breakfast, Lunch, Dinner, Snack }

/// <summary>One slot in a <see cref="MealPlan"/>: a date, an eating occasion, and the recipe planned for
/// it. The recipe is an ordinary (plan-generated) <see cref="Recipe"/>, so every recipe surface —
/// read-aloud, print, cook-along, makeability — works on a planned meal for free.</summary>
public class PlannedMeal : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }

    public int MealPlanId { get; set; }
    public MealPlan? MealPlan { get; set; }

    /// <summary>The recipe planned for this slot — a plan-generated <see cref="Recipe"/>
    /// (see <see cref="Recipe.PlanGenerated"/>).</summary>
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }

    /// <summary>The day this meal is planned for.</summary>
    public DateOnly Date { get; set; }

    public MealSlot Slot { get; set; }
}
